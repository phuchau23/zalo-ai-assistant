using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZaloAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ZaloPersonalProxiesPacing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "auto_brake",
                table: "zalo_personal_accounts",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "custom_invites_per_day",
                table: "zalo_personal_accounts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "custom_messages_per_day",
                table: "zalo_personal_accounts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "custom_new_contacts_per_day",
                table: "zalo_personal_accounts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "outreach_level",
                table: "zalo_personal_accounts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "normal");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "outreach_level_changed_at",
                table: "zalo_personal_accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "outreach_level_reason",
                table: "zalo_personal_accounts",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "outreach_paused_until",
                table: "zalo_personal_accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pacing_mode",
                table: "zalo_personal_accounts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "safe");

            migrationBuilder.AddColumn<Guid>(
                name: "proxy_id",
                table: "zalo_personal_accounts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "proxies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    credentials_enc = table.Column<string>(type: "text", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    endpoint = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    country = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    exit_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    fail_count = table.Column<int>(type: "integer", nullable: false),
                    failing_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cooling_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_proxies", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_zalo_personal_accounts_proxy_id",
                table: "zalo_personal_accounts",
                column: "proxy_id",
                unique: true,
                filter: "proxy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_proxies_fingerprint",
                table: "proxies",
                column: "fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_proxies_status",
                table: "proxies",
                column: "status");

            migrationBuilder.AddForeignKey(
                name: "fk_zalo_personal_accounts_proxies_proxy_id",
                table: "zalo_personal_accounts",
                column: "proxy_id",
                principalTable: "proxies",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_zalo_personal_accounts_proxies_proxy_id",
                table: "zalo_personal_accounts");

            migrationBuilder.DropTable(
                name: "proxies");

            migrationBuilder.DropIndex(
                name: "ix_zalo_personal_accounts_proxy_id",
                table: "zalo_personal_accounts");

            migrationBuilder.DropColumn(
                name: "auto_brake",
                table: "zalo_personal_accounts");

            migrationBuilder.DropColumn(
                name: "custom_invites_per_day",
                table: "zalo_personal_accounts");

            migrationBuilder.DropColumn(
                name: "custom_messages_per_day",
                table: "zalo_personal_accounts");

            migrationBuilder.DropColumn(
                name: "custom_new_contacts_per_day",
                table: "zalo_personal_accounts");

            migrationBuilder.DropColumn(
                name: "outreach_level",
                table: "zalo_personal_accounts");

            migrationBuilder.DropColumn(
                name: "outreach_level_changed_at",
                table: "zalo_personal_accounts");

            migrationBuilder.DropColumn(
                name: "outreach_level_reason",
                table: "zalo_personal_accounts");

            migrationBuilder.DropColumn(
                name: "outreach_paused_until",
                table: "zalo_personal_accounts");

            migrationBuilder.DropColumn(
                name: "pacing_mode",
                table: "zalo_personal_accounts");

            migrationBuilder.DropColumn(
                name: "proxy_id",
                table: "zalo_personal_accounts");
        }
    }
}
