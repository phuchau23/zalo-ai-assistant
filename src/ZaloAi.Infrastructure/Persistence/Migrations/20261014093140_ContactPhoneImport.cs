using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZaloAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContactPhoneImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "consent_at",
                table: "contacts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "consent_marketing",
                table: "contacts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "consent_source",
                table: "contacts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "phone_enc",
                table: "contacts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "phone_hash",
                table: "contacts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "follow_up_done_at",
                table: "contact_notes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_contacts_tenant_id_phone_hash",
                table: "contacts",
                columns: new[] { "tenant_id", "phone_hash" },
                unique: true,
                filter: "phone_hash IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_contacts_tenant_id_phone_hash",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "consent_at",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "consent_marketing",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "consent_source",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "phone_enc",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "phone_hash",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "follow_up_done_at",
                table: "contact_notes");
        }
    }
}
