using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZaloAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NoteFollowUpTime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "follow_up_at",
                table: "contact_notes",
                type: "timestamp with time zone",
                nullable: true);

            // Giữ lịch hẹn cũ (chỉ có ngày): chuyển thành 08:00 giờ Việt Nam của ngày đó.
            migrationBuilder.Sql(
                "UPDATE contact_notes SET follow_up_at = (follow_up_on + time '08:00') AT TIME ZONE 'Asia/Ho_Chi_Minh' WHERE follow_up_on IS NOT NULL;");

            migrationBuilder.DropIndex(
                name: "ix_contact_notes_follow_up_on",
                table: "contact_notes");

            migrationBuilder.DropColumn(
                name: "follow_up_on",
                table: "contact_notes");

            migrationBuilder.CreateIndex(
                name: "ix_contact_notes_follow_up_at",
                table: "contact_notes",
                column: "follow_up_at",
                filter: "follow_up_at IS NOT NULL AND follow_up_queued_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_contact_notes_follow_up_at",
                table: "contact_notes");

            migrationBuilder.DropColumn(
                name: "follow_up_at",
                table: "contact_notes");

            migrationBuilder.AddColumn<DateOnly>(
                name: "follow_up_on",
                table: "contact_notes",
                type: "date",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_contact_notes_follow_up_on",
                table: "contact_notes",
                column: "follow_up_on",
                filter: "follow_up_on IS NOT NULL AND follow_up_queued_at IS NULL");
        }
    }
}
