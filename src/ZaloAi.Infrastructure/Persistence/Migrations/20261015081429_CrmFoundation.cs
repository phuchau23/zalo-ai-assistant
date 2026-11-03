using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZaloAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CrmFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "branch_id",
                table: "memberships",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "permissions",
                table: "memberships",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.CreateTable(
                name: "branches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    phone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_branches", x => x.id);
                    table.ForeignKey(
                        name: "fk_branches_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "catalog_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    group_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    price = table.Column<long>(type: "bigint", nullable: true),
                    price_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    duration_minutes = table.Column<int>(type: "integer", nullable: true),
                    buffer_minutes = table.Column<int>(type: "integer", nullable: false),
                    session_count = table.Column<int>(type: "integer", nullable: true),
                    validity_days = table.Column<int>(type: "integer", nullable: true),
                    sku = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    unit = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    cost_price = table.Column<long>(type: "bigint", nullable: true),
                    track_inventory = table.Column<bool>(type: "boolean", nullable: false),
                    knowledge_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_catalog_items_knowledge_items_knowledge_item_id",
                        column: x => x.knowledge_item_id,
                        principalTable: "knowledge_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_catalog_items_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "crm_settings",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    time_zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    slot_minutes = table.Column<int>(type: "integer", nullable: false),
                    discount_approval_percent = table.Column<int>(type: "integer", nullable: false),
                    default_buffer_minutes = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_crm_settings", x => x.tenant_id);
                    table.ForeignKey(
                        name: "fk_crm_settings_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "branch_closures",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_branch_closures", x => x.id);
                    table.ForeignKey(
                        name: "fk_branch_closures_branches_branch_id",
                        column: x => x.branch_id,
                        principalTable: "branches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_branch_closures_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "branch_hours",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day_of_week = table.Column<int>(type: "integer", nullable: false),
                    open_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    close_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_branch_hours", x => x.id);
                    table.ForeignKey(
                        name: "fk_branch_hours_branches_branch_id",
                        column: x => x.branch_id,
                        principalTable: "branches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_branch_hours_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "staff_members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    phone_enc = table.Column<string>(type: "text", nullable: true),
                    color = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    bookable = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staff_members", x => x.id);
                    table.ForeignKey(
                        name: "fk_staff_members_branches_branch_id",
                        column: x => x.branch_id,
                        principalTable: "branches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_staff_members_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_staff_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "catalog_item_branches",
                columns: table => new
                {
                    catalog_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_item_branches", x => new { x.catalog_item_id, x.branch_id });
                    table.ForeignKey(
                        name: "fk_catalog_item_branches_branches_branch_id",
                        column: x => x.branch_id,
                        principalTable: "branches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_catalog_item_branches_catalog_items_catalog_item_id",
                        column: x => x.catalog_item_id,
                        principalTable: "catalog_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_catalog_item_branches_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "package_contents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    package_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_package_contents", x => x.id);
                    table.ForeignKey(
                        name: "fk_package_contents_catalog_items_package_id",
                        column: x => x.package_id,
                        principalTable: "catalog_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_package_contents_catalog_items_service_id",
                        column: x => x.service_id,
                        principalTable: "catalog_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_package_contents_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "staff_services",
                columns: table => new
                {
                    staff_member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staff_services", x => new { x.staff_member_id, x.catalog_item_id });
                    table.ForeignKey(
                        name: "fk_staff_services_catalog_items_catalog_item_id",
                        column: x => x.catalog_item_id,
                        principalTable: "catalog_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_staff_services_staff_members_staff_member_id",
                        column: x => x.staff_member_id,
                        principalTable: "staff_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_staff_services_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "staff_shift_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    staff_member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day_of_week = table.Column<int>(type: "integer", nullable: false),
                    start_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    end_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staff_shift_templates", x => x.id);
                    table.ForeignKey(
                        name: "fk_staff_shift_templates_staff_members_staff_member_id",
                        column: x => x.staff_member_id,
                        principalTable: "staff_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_staff_shift_templates_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "staff_shifts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    staff_member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    start_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    end_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staff_shifts", x => x.id);
                    table.ForeignKey(
                        name: "fk_staff_shifts_branches_branch_id",
                        column: x => x.branch_id,
                        principalTable: "branches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_staff_shifts_staff_members_staff_member_id",
                        column: x => x.staff_member_id,
                        principalTable: "staff_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_staff_shifts_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "staff_time_off",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    staff_member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    to_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staff_time_off", x => x.id);
                    table.ForeignKey(
                        name: "fk_staff_time_off_staff_members_staff_member_id",
                        column: x => x.staff_member_id,
                        principalTable: "staff_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_staff_time_off_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_memberships_branch_id",
                table: "memberships",
                column: "branch_id");

            migrationBuilder.CreateIndex(
                name: "ix_branch_closures_branch_id",
                table: "branch_closures",
                column: "branch_id");

            migrationBuilder.CreateIndex(
                name: "ix_branch_closures_tenant_id_branch_id_date",
                table: "branch_closures",
                columns: new[] { "tenant_id", "branch_id", "date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_branch_hours_branch_id",
                table: "branch_hours",
                column: "branch_id");

            migrationBuilder.CreateIndex(
                name: "ix_branch_hours_tenant_id_branch_id_day_of_week",
                table: "branch_hours",
                columns: new[] { "tenant_id", "branch_id", "day_of_week" });

            migrationBuilder.CreateIndex(
                name: "ix_branches_tenant_id",
                table: "branches",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_branches_tenant_id_code",
                table: "branches",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_catalog_item_branches_branch_id",
                table: "catalog_item_branches",
                column: "branch_id");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_item_branches_tenant_id",
                table: "catalog_item_branches",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_items_knowledge_item_id",
                table: "catalog_items",
                column: "knowledge_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_items_tenant_id_code",
                table: "catalog_items",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_catalog_items_tenant_id_kind_is_active",
                table: "catalog_items",
                columns: new[] { "tenant_id", "kind", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_package_contents_package_id",
                table: "package_contents",
                column: "package_id");

            migrationBuilder.CreateIndex(
                name: "ix_package_contents_service_id",
                table: "package_contents",
                column: "service_id");

            migrationBuilder.CreateIndex(
                name: "ix_package_contents_tenant_id_package_id_service_id",
                table: "package_contents",
                columns: new[] { "tenant_id", "package_id", "service_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_staff_members_branch_id",
                table: "staff_members",
                column: "branch_id");

            migrationBuilder.CreateIndex(
                name: "ix_staff_members_tenant_id_branch_id",
                table: "staff_members",
                columns: new[] { "tenant_id", "branch_id" });

            migrationBuilder.CreateIndex(
                name: "ix_staff_members_tenant_id_user_id",
                table: "staff_members",
                columns: new[] { "tenant_id", "user_id" },
                unique: true,
                filter: "user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_staff_members_user_id",
                table: "staff_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_staff_services_catalog_item_id",
                table: "staff_services",
                column: "catalog_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_staff_services_tenant_id",
                table: "staff_services",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_staff_shift_templates_staff_member_id",
                table: "staff_shift_templates",
                column: "staff_member_id");

            migrationBuilder.CreateIndex(
                name: "ix_staff_shift_templates_tenant_id_staff_member_id_day_of_week",
                table: "staff_shift_templates",
                columns: new[] { "tenant_id", "staff_member_id", "day_of_week" });

            migrationBuilder.CreateIndex(
                name: "ix_staff_shifts_branch_id",
                table: "staff_shifts",
                column: "branch_id");

            migrationBuilder.CreateIndex(
                name: "ix_staff_shifts_staff_member_id",
                table: "staff_shifts",
                column: "staff_member_id");

            migrationBuilder.CreateIndex(
                name: "ix_staff_shifts_tenant_id_branch_id_date",
                table: "staff_shifts",
                columns: new[] { "tenant_id", "branch_id", "date" });

            migrationBuilder.CreateIndex(
                name: "ix_staff_shifts_tenant_id_staff_member_id_date",
                table: "staff_shifts",
                columns: new[] { "tenant_id", "staff_member_id", "date" });

            migrationBuilder.CreateIndex(
                name: "ix_staff_time_off_staff_member_id",
                table: "staff_time_off",
                column: "staff_member_id");

            migrationBuilder.CreateIndex(
                name: "ix_staff_time_off_tenant_id_staff_member_id_from_at",
                table: "staff_time_off",
                columns: new[] { "tenant_id", "staff_member_id", "from_at" });

            migrationBuilder.AddForeignKey(
                name: "fk_memberships_branches_branch_id",
                table: "memberships",
                column: "branch_id",
                principalTable: "branches",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_memberships_branches_branch_id",
                table: "memberships");

            migrationBuilder.DropTable(
                name: "branch_closures");

            migrationBuilder.DropTable(
                name: "branch_hours");

            migrationBuilder.DropTable(
                name: "catalog_item_branches");

            migrationBuilder.DropTable(
                name: "crm_settings");

            migrationBuilder.DropTable(
                name: "package_contents");

            migrationBuilder.DropTable(
                name: "staff_services");

            migrationBuilder.DropTable(
                name: "staff_shift_templates");

            migrationBuilder.DropTable(
                name: "staff_shifts");

            migrationBuilder.DropTable(
                name: "staff_time_off");

            migrationBuilder.DropTable(
                name: "catalog_items");

            migrationBuilder.DropTable(
                name: "staff_members");

            migrationBuilder.DropTable(
                name: "branches");

            migrationBuilder.DropIndex(
                name: "ix_memberships_branch_id",
                table: "memberships");

            migrationBuilder.DropColumn(
                name: "branch_id",
                table: "memberships");

            migrationBuilder.DropColumn(
                name: "permissions",
                table: "memberships");
        }
    }
}
