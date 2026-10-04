using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZaloAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProactiveCare : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "proactive",
                table: "messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "care_auto_send",
                table: "handoff_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "care_send_end",
                table: "handoff_settings",
                type: "character varying(5)",
                maxLength: 5,
                nullable: false,
                defaultValue: "20:00");

            migrationBuilder.AddColumn<string>(
                name: "care_send_start",
                table: "handoff_settings",
                type: "character varying(5)",
                maxLength: 5,
                nullable: false,
                defaultValue: "08:00");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_proactive_at",
                table: "contacts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "proactive_awaiting_reply",
                table: "contacts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "proactive_opt_out_at",
                table: "contacts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "proactive_opt_out_source",
                table: "contacts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "escalation_reason",
                table: "care_suggestions",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "scheduled_send_at",
                table: "care_suggestions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "sent_message_id",
                table: "care_suggestions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_care_suggestions_scheduled_send_at",
                table: "care_suggestions",
                column: "scheduled_send_at",
                filter: "status = 'open' AND scheduled_send_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_care_suggestions_sent_message_id",
                table: "care_suggestions",
                column: "sent_message_id");

            migrationBuilder.AddForeignKey(
                name: "fk_care_suggestions_messages_sent_message_id",
                table: "care_suggestions",
                column: "sent_message_id",
                principalTable: "messages",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_care_suggestions_messages_sent_message_id",
                table: "care_suggestions");

            migrationBuilder.DropIndex(
                name: "ix_care_suggestions_scheduled_send_at",
                table: "care_suggestions");

            migrationBuilder.DropIndex(
                name: "ix_care_suggestions_sent_message_id",
                table: "care_suggestions");

            migrationBuilder.DropColumn(
                name: "proactive",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "care_auto_send",
                table: "handoff_settings");

            migrationBuilder.DropColumn(
                name: "care_send_end",
                table: "handoff_settings");

            migrationBuilder.DropColumn(
                name: "care_send_start",
                table: "handoff_settings");

            migrationBuilder.DropColumn(
                name: "last_proactive_at",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "proactive_awaiting_reply",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "proactive_opt_out_at",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "proactive_opt_out_source",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "escalation_reason",
                table: "care_suggestions");

            migrationBuilder.DropColumn(
                name: "scheduled_send_at",
                table: "care_suggestions");

            migrationBuilder.DropColumn(
                name: "sent_message_id",
                table: "care_suggestions");
        }
    }
}
