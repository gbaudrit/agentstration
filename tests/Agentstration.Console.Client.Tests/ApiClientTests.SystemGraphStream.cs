using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

public sealed partial class ApiClientTests
{
    [TestMethod]
    public async Task GraphFramesAreReadableBeforeTheBodyEndsAndCallerCancellationClosesTheStream()
    {
        var actor = ResourceScopeRef.Workspace(Guid.NewGuid()); var epoch = Guid.NewGuid();
        var body = new GraphBody();
        body.Push(new(epoch, 1, "start", DateTimeOffset.UnixEpoch, [], [], []));
        using var handler = new StubHandler(request =>
        {
            Assert.AreEqual("/api/system-graph/stream", request.RequestUri!.AbsolutePath);
            Assert.Contains("namespace=finance", request.RequestUri.Query);
            Assert.Contains("root=publication%3Auid%3A1%2F2", request.RequestUri.Query);
            Assert.AreEqual(actor.Value, request.Headers.GetValues("X-Agentstration-Context").Single());
            return new(HttpStatusCode.OK) { Content = new StreamContent(body) };
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/"), Timeout = TimeSpan.FromSeconds(1) };
        var client = new SystemExplorerApiClient(http);
        using var lifetime = new CancellationTokenSource();
        var reader = client.StreamAsync(actor, new("finance"), "publication:uid:1/2", lifetime.Token).GetAsyncEnumerator(lifetime.Token);
        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual("start", reader.Current.Kind);
        var next = reader.MoveNextAsync().AsTask();
        await body.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(next.IsCompleted);
        body.Push(new(epoch, 2, "append", DateTimeOffset.UnixEpoch, [GraphNode(actor, "visible")], [], []));
        Assert.IsTrue(await next); Assert.AreEqual("visible", reader.Current.Nodes.Single().Identity.Name);
        next = reader.MoveNextAsync().AsTask();
        await lifetime.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await next);
        await reader.DisposeAsync();
        Assert.IsTrue(body.Closed);
    }

    [TestMethod]
    public async Task AQuietBodyHasAnIdleDeadlineRatherThanAnUnlimitedRead()
    {
        var body = new GraphBody(); var epoch = Guid.NewGuid(); var clock = new GraphClock();
        body.Push(new(epoch, 1, "start", DateTimeOffset.UnixEpoch, [], [], []));
        using var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(body) });
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        var client = new SystemExplorerApiClient(http, clock);
        var reader = client.StreamAsync(ResourceScopeRef.Instance, ResourceNamespace.Default, null, default).GetAsyncEnumerator();
        Assert.IsTrue(await reader.MoveNextAsync());
        var pending = reader.MoveNextAsync().AsTask();
        await body.Waiting.Task;
        clock.Advance(TimeSpan.FromSeconds(31));
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
        await reader.DisposeAsync();
        Assert.IsTrue(body.Closed);
    }

    [TestMethod]
    public void StreamBufferKeepsExactIdentitiesAndClearsOnGapsErrorsOrDanglingEndpoints()
    {
        var owner = ResourceScopeRef.Workspace(Guid.NewGuid()); var a = GraphNode(owner, "same"); var b = GraphNode(owner, "same");
        foreach (var fault in new[] { "gap", "epoch", "error", "restart", "dangling", "changed" })
        {
            var epoch = Guid.NewGuid(); var buffer = new SystemGraphStreamBuffer();
            Assert.IsTrue(buffer.Apply(new(epoch, 1, "start", DateTimeOffset.UnixEpoch, [], [], [])));
            Assert.IsTrue(buffer.Apply(new(epoch, 2, "append", DateTimeOffset.UnixEpoch, [a], [], [new(owner, null)])));
            Assert.IsTrue(buffer.Apply(new(epoch, 3, "append", DateTimeOffset.UnixEpoch, [a, b], [new(a.Id, b.Id, "exact-dependency")], [])));
            Assert.AreEqual(2, buffer.Count); Assert.IsFalse(buffer.Page!.GloballyComplete);
            var frame = fault switch
            {
                "gap" => new SystemGraphStreamFrame(epoch, 5, "heartbeat", DateTimeOffset.UnixEpoch, [], [], []),
                "epoch" => new(Guid.NewGuid(), 4, "heartbeat", DateTimeOffset.UnixEpoch, [], [], []),
                "error" => new(epoch, 4, "error", DateTimeOffset.UnixEpoch, [], [], [], ErrorCode: "system_graph_changed"),
                "restart" => new(epoch, 4, "start", DateTimeOffset.UnixEpoch, [], [], []),
                "dangling" => new(epoch, 4, "append", DateTimeOffset.UnixEpoch, [], [new(a.Id, "unknown", "exact-dependency")], []),
                _ => new(epoch, 4, "append", DateTimeOffset.UnixEpoch, [a with { Evidence = new(99) }], [], [])
            };
            Assert.IsFalse(buffer.Apply(frame), fault); Assert.AreEqual(0, buffer.Count); Assert.IsNull(buffer.Page);
        }
    }

    private static SystemGraphNode GraphNode(ResourceScopeRef owner, string name)
    {
        var uid = Guid.NewGuid(); return new(SystemGraphIds.Definition(uid), new(uid, owner, ResourceNamespace.Default, "Agent", name), "definition");
    }
    private sealed class GraphBody : Stream
    {
        private readonly Channel<byte[]> blocks = Channel.CreateUnbounded<byte[]>();
        private byte[]? current;
        private int offset;
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Closed { get; private set; }
        public void Push(SystemGraphStreamFrame frame) => blocks.Writer.TryWrite(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(frame, JsonSerializerOptions.Web) + "\n"));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (current is null || offset == current.Length)
            {
                if (!blocks.Reader.TryRead(out current))
                {
                    Waiting.TrySetResult();
                    if (!await blocks.Reader.WaitToReadAsync(ct)) return 0;
                    current = await blocks.Reader.ReadAsync(ct);
                }
                offset = 0;
            }
            var count = Math.Min(buffer.Length, current.Length - offset);
            current.AsMemory(offset, count).CopyTo(buffer); offset += count; return count;
        }
        protected override void Dispose(bool disposing) { Closed = true; blocks.Writer.TryComplete(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class GraphClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        private readonly List<ClockTimer> timers = [];
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new ClockTimer(this, callback, state); timers.Add(timer); timer.Change(dueTime, period); return timer; }
        public void Advance(TimeSpan elapsed) { now += elapsed; foreach (var timer in timers.ToArray()) timer.Fire(); }
        private sealed class ClockTimer(GraphClock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? due;
            public bool Change(TimeSpan dueTime, TimeSpan period) { due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.now + dueTime; return true; }
            public void Fire() { if (due is { } at && at <= clock.now) { due = null; callback(state); } }
            public void Dispose() => due = null;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
