namespace Agentstration.Bootstrap.Contracts;

public class BootstrapRequestException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
