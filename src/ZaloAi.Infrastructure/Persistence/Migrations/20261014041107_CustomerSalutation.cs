using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZaloAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CustomerSalutation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "bot_tone",
                table: "tenants",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "human",
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldDefaultValue: "friendly");

            migrationBuilder.AddColumn<string>(
                name: "salutation",
                table: "contacts",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "salutation_source",
                table: "contacts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "salutation",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "salutation_source",
                table: "contacts");

            migrationBuilder.AlterColumn<string>(
                name: "bot_tone",
                table: "tenants",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "friendly",
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldDefaultValue: "human");
        }
    }
}
