using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZaloAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CrmAppointments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "booking_lead_minutes",
                table: "crm_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "bot_booking_enabled",
                table: "crm_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "bot_max_party_size",
                table: "crm_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "cancel_lead_hours",
                table: "crm_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "max_booking_days_ahead",
                table: "crm_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "reminder1_hours",
                table: "crm_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "reminder2_hours",
                table: "crm_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "appointment_series",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    interval_days = table.Column<int>(type: "integer", nullable: false),
                    occurrences = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_appointment_series", x => x.id);
                    table.ForeignKey(
                        name: "fk_appointment_series_contacts_contact_id",
                        column: x => x.contact_id,
                        principalTable: "contacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_appointment_series_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "resources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_resources", x => x.id);
                    table.ForeignKey(
                        name: "fk_resources_branches_branch_id",
                        column: x => x.branch_id,
                        principalTable: "branches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_resources_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "waitlist_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    staff_member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    preferred_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    preferred_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    note_enc = table.Column<string>(type: "text", nullable: true),
                    offered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    offered_slot_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_waitlist_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_waitlist_entries_branches_branch_id",
                        column: x => x.branch_id,
                        principalTable: "branches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_waitlist_entries_catalog_items_catalog_item_id",
                        column: x => x.catalog_item_id,
                        principalTable: "catalog_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_waitlist_entries_contacts_contact_id",
                        column: x => x.contact_id,
                        principalTable: "contacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_waitlist_entries_staff_members_staff_member_id",
                        column: x => x.staff_member_id,
                        principalTable: "staff_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_waitlist_entries_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "appointments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    staff_member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: true),
                    start_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    end_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    buffer_minutes = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    booked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    series_id = table.Column<Guid>(type: "uuid", nullable: true),
                    party_size = table.Column<int>(type: "integer", nullable: false),
                    note_enc = table.Column<string>(type: "text", nullable: true),
                    ai_brief_enc = table.Column<string>(type: "text", nullable: true),
                    ai_brief_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    cancelled_by = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reminder1sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reminder2sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    customer_confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    arrived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    done_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    after_care_queued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_appointments", x => x.id);
                    table.ForeignKey(
                        name: "fk_appointments_appointment_series_series_id",
                        column: x => x.series_id,
                        principalTable: "appointment_series",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_appointments_branches_branch_id",
                        column: x => x.branch_id,
                        principalTable: "branches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_appointments_contacts_contact_id",
                        column: x => x.contact_id,
                        principalTable: "contacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_appointments_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_appointments_resources_resource_id",
                        column: x => x.resource_id,
                        principalTable: "resources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_appointments_staff_members_staff_member_id",
                        column: x => x.staff_member_id,
                        principalTable: "staff_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_appointments_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_appointments_users_booked_by_user_id",
                        column: x => x.booked_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "appointment_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    appointment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_appointment_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_appointment_events_appointments_appointment_id",
                        column: x => x.appointment_id,
                        principalTable: "appointments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_appointment_events_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "appointment_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    appointment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    price_snapshot = table.Column<long>(type: "bigint", nullable: true),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_appointment_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_appointment_items_appointments_appointment_id",
                        column: x => x.appointment_id,
                        principalTable: "appointments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_appointment_items_catalog_items_catalog_item_id",
                        column: x => x.catalog_item_id,
                        principalTable: "catalog_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_appointment_items_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_appointment_events_appointment_id",
                table: "appointment_events",
                column: "appointment_id");

            migrationBuilder.CreateIndex(
                name: "ix_appointment_events_tenant_id_appointment_id_at",
                table: "appointment_events",
                columns: new[] { "tenant_id", "appointment_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_appointment_items_appointment_id",
                table: "appointment_items",
                column: "appointment_id");

            migrationBuilder.CreateIndex(
                name: "ix_appointment_items_catalog_item_id",
                table: "appointment_items",
                column: "catalog_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_appointment_items_tenant_id_appointment_id",
                table: "appointment_items",
                columns: new[] { "tenant_id", "appointment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_appointment_series_contact_id",
                table: "appointment_series",
                column: "contact_id");

            migrationBuilder.CreateIndex(
                name: "ix_appointment_series_tenant_id_contact_id",
                table: "appointment_series",
                columns: new[] { "tenant_id", "contact_id" });

            migrationBuilder.CreateIndex(
                name: "ix_appointments_booked_by_user_id",
                table: "appointments",
                column: "booked_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_appointments_branch_id",
                table: "appointments",
                column: "branch_id");

            migrationBuilder.CreateIndex(
                name: "ix_appointments_contact_id",
                table: "appointments",
                column: "contact_id");

            migrationBuilder.CreateIndex(
                name: "ix_appointments_conversation_id",
                table: "appointments",
                column: "conversation_id");

            migrationBuilder.CreateIndex(
                name: "ix_appointments_resource_id",
                table: "appointments",
                column: "resource_id");

            migrationBuilder.CreateIndex(
                name: "ix_appointments_series_id",
                table: "appointments",
                column: "series_id");

            migrationBuilder.CreateIndex(
                name: "ix_appointments_staff_member_id",
                table: "appointments",
                column: "staff_member_id");

            migrationBuilder.CreateIndex(
                name: "ix_appointments_tenant_id_branch_id_start_at",
                table: "appointments",
                columns: new[] { "tenant_id", "branch_id", "start_at" });

            migrationBuilder.CreateIndex(
                name: "ix_appointments_tenant_id_contact_id_start_at",
                table: "appointments",
                columns: new[] { "tenant_id", "contact_id", "start_at" });

            migrationBuilder.CreateIndex(
                name: "ix_appointments_tenant_id_staff_member_id_start_at",
                table: "appointments",
                columns: new[] { "tenant_id", "staff_member_id", "start_at" });

            migrationBuilder.CreateIndex(
                name: "ix_appointments_tenant_id_status_start_at",
                table: "appointments",
                columns: new[] { "tenant_id", "status", "start_at" });

            migrationBuilder.CreateIndex(
                name: "ix_resources_branch_id",
                table: "resources",
                column: "branch_id");

            migrationBuilder.CreateIndex(
                name: "ix_resources_tenant_id_branch_id",
                table: "resources",
                columns: new[] { "tenant_id", "branch_id" });

            migrationBuilder.CreateIndex(
                name: "ix_waitlist_entries_branch_id",
                table: "waitlist_entries",
                column: "branch_id");

            migrationBuilder.CreateIndex(
                name: "ix_waitlist_entries_catalog_item_id",
                table: "waitlist_entries",
                column: "catalog_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_waitlist_entries_contact_id",
                table: "waitlist_entries",
                column: "contact_id");

            migrationBuilder.CreateIndex(
                name: "ix_waitlist_entries_staff_member_id",
                table: "waitlist_entries",
                column: "staff_member_id");

            migrationBuilder.CreateIndex(
                name: "ix_waitlist_entries_tenant_id_branch_id_status_preferred_from",
                table: "waitlist_entries",
                columns: new[] { "tenant_id", "branch_id", "status", "preferred_from" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "appointment_events");

            migrationBuilder.DropTable(
                name: "appointment_items");

            migrationBuilder.DropTable(
                name: "waitlist_entries");

            migrationBuilder.DropTable(
                name: "appointments");

            migrationBuilder.DropTable(
                name: "appointment_series");

            migrationBuilder.DropTable(
                name: "resources");

            migrationBuilder.DropColumn(
                name: "booking_lead_minutes",
                table: "crm_settings");

            migrationBuilder.DropColumn(
                name: "bot_booking_enabled",
                table: "crm_settings");

            migrationBuilder.DropColumn(
                name: "bot_max_party_size",
                table: "crm_settings");

            migrationBuilder.DropColumn(
                name: "cancel_lead_hours",
                table: "crm_settings");

            migrationBuilder.DropColumn(
                name: "max_booking_days_ahead",
                table: "crm_settings");

            migrationBuilder.DropColumn(
                name: "reminder1_hours",
                table: "crm_settings");

            migrationBuilder.DropColumn(
                name: "reminder2_hours",
                table: "crm_settings");
        }
    }
}
