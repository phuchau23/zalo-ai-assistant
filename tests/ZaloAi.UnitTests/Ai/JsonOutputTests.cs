using Shouldly;
using ZaloAi.Ai.Bot;
using ZaloAi.Ai.Care;
using ZaloAi.Ai.Providers;
using ZaloAi.Ai.Reports;

namespace ZaloAi.UnitTests.Ai;

public sealed class JsonOutputTests
{
    private const string Schema = """
        {
          "type": "object",
          "properties": {
            "reply": { "type": "string" },
            "count": { "type": "integer" },
            "level": { "type": ["string", "null"], "enum": ["day", "week", null] },
            "tags": { "type": "array", "items": { "type": "object", "properties": { "k": { "type": "string" } }, "required": ["k"] } },
            "booking": { "type": ["object", "null"], "properties": { "date": { "type": ["string", "null"] } }, "required": ["date"] }
          },
          "required": ["reply", "count", "level", "tags", "booking"]
        }
        """;

    private const string Valid = """{"reply":"ok","count":1,"level":null,"tags":[{"k":"a"}],"booking":{"date":null}}""";

    [Fact]
    public void Valid_output_is_returned_unchanged() => JsonOutput.Validate(Valid, Schema).ShouldBe(Valid);

    [Theory]
    [InlineData("```json\n" + Valid + "\n```")]
    [InlineData("```\n" + Valid + "\n```")]
    [InlineData("Đây là JSON: " + Valid + " — hết.")]
    public void Fences_and_extra_text_are_cleaned(string text) => JsonOutput.Validate(text, Schema).ShouldBe(Valid);

    [Theory]
    [InlineData("""{"reply":"ok","count":1,"level":null,"tags":[]}""")] // thiếu booking
    [InlineData("""{"reply":"ok","count":1.5,"level":null,"tags":[],"booking":null}""")] // không phải số nguyên
    [InlineData("""{"reply":"ok","count":1,"level":"year","tags":[],"booking":null}""")] // ngoài enum
    [InlineData("""{"reply":"ok","count":1,"level":null,"tags":[{"x":"a"}],"booking":null}""")] // phần tử thiếu k
    [InlineData("""{"reply":"ok","count":1,"level":null,"tags":[],"booking":{"date":5}}""")] // sai kiểu lồng
    [InlineData("""{"reply":3,"count":1,"level":null,"tags":[],"booking":null}""")]
    [InlineData("""["reply"]""")]
    [InlineData("không phải JSON")]
    [InlineData("""{"reply":"ok",""")]
    public void Wrong_shape_is_rejected(string text) => JsonOutput.Validate(text, Schema).ShouldBeNull();

    [Fact]
    public void Extra_fields_are_allowed() =>
        JsonOutput.Validate("""{"reply":"ok","count":1,"level":"week","tags":[],"booking":null,"extra":true}""", Schema).ShouldNotBeNull();

    [Fact]
    public void Project_schemas_accept_answers_in_their_shape()
    {
        JsonOutput.Validate(
            """{"messages":["Dạ"],"customer_salutation":"none","used_chunk_ids":[],"confidence":"high","needs_human":false,"handoff_reason":null,"urgency":"none","health_topic":false,"sentiment":"neutral","lead_fields":[],"booking":{"action":"book","service_codes":["DV-1"],"date":"2026-11-04","time":null,"staff_name":null,"branch_name":null,"party_size":1,"appointment_id":null}}""",
            PromptBuilder.ReplySchema).ShouldNotBeNull();
        JsonOutput.Validate(
            """{"action":"send","human_reason":null,"promotional":false,"temperature":"warm","trigger":"other","reason":"r","suggested_action":"s","draft_message":"d"}""",
            CareAdvisor.Schema).ShouldNotBeNull();
        JsonOutput.Validate(
            """{"report":"revenue","from":null,"to":null,"group_by":null,"focus":null,"unanswerable":false,"explanation":null}""",
            ReportQuestionInterpreter.Schema).ShouldNotBeNull();
    }
}
