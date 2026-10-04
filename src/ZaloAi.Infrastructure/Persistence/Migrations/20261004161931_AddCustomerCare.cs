using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZaloAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerCare : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "care_cold_hours",
                table: "handoff_settings",
                type: "integer",
                nullable: false,
                defaultValue: 6);

            migrationBuilder.AddColumn<bool>(
                name: "care_enabled",
                table: "handoff_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "care_analyzed_at",
                table: "conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_customer_message_at",
                table: "contacts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lead_status",
                table: "contacts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "new");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "lead_status_changed_at",
                table: "contacts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "lead_status_manual",
                table: "contacts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<List<string>>(
                name: "tags",
                table: "contacts",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.CreateTable(
                name: "care_suggestions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    temperature = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    trigger = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    reason_enc = table.Column<string>(type: "text", nullable: false),
                    suggested_action_enc = table.Column<string>(type: "text", nullable: true),
                    draft_enc = table.Column<string>(type: "text", nullable: true),
                    messaging_deadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    assigned_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    outcome = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    resolved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_care_suggestions", x => x.id);
                    table.ForeignKey(
                        name: "fk_care_suggestions_contacts_contact_id",
                        column: x => x.contact_id,
                        principalTable: "contacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_care_suggestions_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_care_suggestions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_care_suggestions_users_assigned_user_id",
                        column: x => x.assigned_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_care_suggestions_users_resolved_by_user_id",
                        column: x => x.resolved_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "contact_notes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    content_enc = table.Column<string>(type: "text", nullable: false),
                    happened_on = table.Column<DateOnly>(type: "date", nullable: false),
                    follow_up_on = table.Column<DateOnly>(type: "date", nullable: true),
                    follow_up_queued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_contact_notes", x => x.id);
                    table.ForeignKey(
                        name: "fk_contact_notes_contacts_contact_id",
                        column: x => x.contact_id,
                        principalTable: "contacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_contact_notes_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_contact_notes_users_author_user_id",
                        column: x => x.author_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_contacts_tenant_id_last_customer_message_at",
                table: "contacts",
                columns: new[] { "tenant_id", "last_customer_message_at" });

            migrationBuilder.CreateIndex(
                name: "ix_contacts_tenant_id_lead_status",
                table: "contacts",
                columns: new[] { "tenant_id", "lead_status" });

            migrationBuilder.CreateIndex(
                name: "ix_care_suggestions_assigned_user_id",
                table: "care_suggestions",
                column: "assigned_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_care_suggestions_contact_id",
                table: "care_suggestions",
                column: "contact_id");

            migrationBuilder.CreateIndex(
                name: "ix_care_suggestions_conversation_id",
                table: "care_suggestions",
                column: "conversation_id");

            migrationBuilder.CreateIndex(
                name: "ix_care_suggestions_one_open_per_contact",
                table: "care_suggestions",
                columns: new[] { "tenant_id", "contact_id" },
                unique: true,
                filter: "status = 'open'");

            migrationBuilder.CreateIndex(
                name: "ix_care_suggestions_resolved_by_user_id",
                table: "care_suggestions",
                column: "resolved_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_care_suggestions_tenant_id_status_messaging_deadline",
                table: "care_suggestions",
                columns: new[] { "tenant_id", "status", "messaging_deadline" });

            migrationBuilder.CreateIndex(
                name: "ix_contact_notes_author_user_id",
                table: "contact_notes",
                column: "author_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_contact_notes_contact_id",
                table: "contact_notes",
                column: "contact_id");

            migrationBuilder.CreateIndex(
                name: "ix_contact_notes_follow_up_on",
                table: "contact_notes",
                column: "follow_up_on",
                filter: "follow_up_on IS NOT NULL AND follow_up_queued_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_contact_notes_tenant_id_contact_id_happened_on",
                table: "contact_notes",
                columns: new[] { "tenant_id", "contact_id", "happened_on" });

            // Giá trị cho dòng cũ: mốc tin cuối của khách lấy từ các hội thoại đã có.
            migrationBuilder.Sql(
                "UPDATE contacts c SET last_customer_message_at = x.last FROM (SELECT contact_id, max(last_customer_message_at) AS last FROM conversations GROUP BY contact_id) x WHERE x.contact_id = c.id;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "care_suggestions");

            migrationBuilder.DropTable(
                name: "contact_notes");

            migrationBuilder.DropIndex(
                name: "ix_contacts_tenant_id_last_customer_message_at",
                table: "contacts");

            migrationBuilder.DropIndex(
                name: "ix_contacts_tenant_id_lead_status",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "care_cold_hours",
                table: "handoff_settings");

            migrationBuilder.DropColumn(
                name: "care_enabled",
                table: "handoff_settings");

            migrationBuilder.DropColumn(
                name: "care_analyzed_at",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "last_customer_message_at",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "lead_status",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "lead_status_changed_at",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "lead_status_manual",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "tags",
                table: "contacts");
        }
    }
}
