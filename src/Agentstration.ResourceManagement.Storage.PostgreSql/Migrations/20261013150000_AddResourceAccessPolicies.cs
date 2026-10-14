using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentstration.ResourceManagement.Storage.PostgreSql.Migrations;

[DbContext(typeof(ResourceManagementDbContext))]
[Migration("20261013150000_AddResourceAccessPolicies")]
public sealed class AddResourceAccessPolicies : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ResourceAccessPolicies",
            schema: "management",
            columns: table => new
            {
                ResourceUid = table.Column<Guid>(type: "uuid", nullable: false),
                OwnerScopeRef = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Namespace = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Kind = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Generation = table.Column<long>(type: "bigint", nullable: true),
                Version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                GrantsJson = table.Column<string>(type: "text", nullable: false),
                ETag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_ResourceAccessPolicies", value => value.ResourceUid));

        migrationBuilder.CreateIndex(
            name: "IX_ResourceAccessPolicies_OwnerScopeRef",
            schema: "management",
            table: "ResourceAccessPolicies",
            column: "OwnerScopeRef");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "ResourceAccessPolicies", schema: "management");
}
