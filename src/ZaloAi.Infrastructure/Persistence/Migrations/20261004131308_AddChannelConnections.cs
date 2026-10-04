using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZaloAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "delivery_error",
                table: "messages",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "delivery_status",
                table: "messages",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "none");

            migrationBuilder.AddColumn<Guid>(
                name: "connection_id",
                table: "conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "channel_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    external_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    access_token_enc = table.Column<string>(type: "text", nullable: false),
                    refresh_token_enc = table.Column<string>(type: "text", nullable: false),
                    access_token_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    refresh_token_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    last_error = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    connected_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_refreshed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_channel_connections", x => x.id);
                    table.ForeignKey(
                        name: "fk_channel_connections_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_conversations_connection_id",
                table: "conversations",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "ix_conversations_tenant_id_connection_id_contact_id",
                table: "conversations",
                columns: new[] { "tenant_id", "connection_id", "contact_id" });

            migrationBuilder.CreateIndex(
                name: "ix_channel_connections_channel_external_id",
                table: "channel_connections",
                columns: new[] { "channel", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_channel_connections_status_access_token_expires_at",
                table: "channel_connections",
                columns: new[] { "status", "access_token_expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_channel_connections_tenant_id_channel",
                table: "channel_connections",
                columns: new[] { "tenant_id", "channel" });

            migrationBuilder.AddForeignKey(
                name: "fk_conversations_channel_connections_connection_id",
                table: "conversations",
                column: "connection_id",
                principalTable: "channel_connections",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_conversations_channel_connections_connection_id",
                table: "conversations");

            migrationBuilder.DropTable(
                name: "channel_connections");

            migrationBuilder.DropIndex(
                name: "ix_conversations_connection_id",
                table: "conversations");

            migrationBuilder.DropIndex(
                name: "ix_conversations_tenant_id_connection_id_contact_id",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "delivery_error",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "delivery_status",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "connection_id",
                table: "conversations");
        }
    }
}
