using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZaloAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "sender_user_id",
                table: "messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "assigned_user_id",
                table: "conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_message_at",
                table: "conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_reminder_at",
                table: "conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "needs_attention_since",
                table: "conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "handoff_settings",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    handoff_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    after_hours_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    takeover_message_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    takeover_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    return_to_bot_message_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    return_to_bot_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    staff_signature_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    response_time = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    open_time = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    close_time = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    working_days = table.Column<int>(type: "integer", nullable: false),
                    reminder_minutes = table.Column<int>(type: "integer", nullable: false),
                    telegram_chat_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_handoff_settings", x => x.tenant_id);
                    table.ForeignKey(
                        name: "fk_handoff_settings_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_messages_sender_user_id",
                table: "messages",
                column: "sender_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_conversations_assigned_user_id",
                table: "conversations",
                column: "assigned_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_conversations_needs_attention_since",
                table: "conversations",
                column: "needs_attention_since",
                filter: "needs_attention_since IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_conversations_tenant_id_last_message_at",
                table: "conversations",
                columns: new[] { "tenant_id", "last_message_at" });

            migrationBuilder.AddForeignKey(
                name: "fk_conversations_users_assigned_user_id",
                table: "conversations",
                column: "assigned_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_messages_users_sender_user_id",
                table: "messages",
                column: "sender_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_conversations_users_assigned_user_id",
                table: "conversations");

            migrationBuilder.DropForeignKey(
                name: "fk_messages_users_sender_user_id",
                table: "messages");

            migrationBuilder.DropTable(
                name: "handoff_settings");

            migrationBuilder.DropIndex(
                name: "ix_messages_sender_user_id",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "ix_conversations_assigned_user_id",
                table: "conversations");

            migrationBuilder.DropIndex(
                name: "ix_conversations_needs_attention_since",
                table: "conversations");

            migrationBuilder.DropIndex(
                name: "ix_conversations_tenant_id_last_message_at",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "sender_user_id",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "assigned_user_id",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "last_message_at",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "last_reminder_at",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "needs_attention_since",
                table: "conversations");
        }
    }
}
