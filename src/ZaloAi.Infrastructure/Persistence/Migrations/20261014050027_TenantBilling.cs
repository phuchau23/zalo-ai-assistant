using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZaloAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "conversation_quota",
                table: "tenants",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "suspended_reason",
                table: "tenants",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "conversation_quota",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "suspended_reason",
                table: "tenants");
        }
    }
}
