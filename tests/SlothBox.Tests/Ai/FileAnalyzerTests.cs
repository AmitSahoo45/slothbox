using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SlothBox.Ai.Analyze;
using SlothBox.Core;

namespace SlothBox.Tests.Ai;

public sealed class FileAnalyzerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The output schema exactly as spec 5.4 writes it.
    private const string SpecSchema = """
        {
          "type": "object",
          "required": ["type", "category", "title", "caption", "text", "receipt"],
          "properties": {
            "type":     { "type": "string", "enum": ["receipt", "screenshot", "document", "unknown"] },
            "category": { "type": "string" },
            "title":    { "type": "string" },
            "caption":  { "type": "string" },
            "text":     { "type": "string" },
            "receipt": {
              "type": ["object", "null"],
              "properties": {
                "vendor":         { "type": ["string", "null"] },
                "date":           { "type": ["string", "null"] },
                "amount":         { "type": ["number", "null"] },
                "currency":       { "type": ["string", "null"] },
                "payment_method": { "type": ["string", "null"], "enum": ["upi", "card", "cash", "netbanking", "unknown", null] }
              }
            }
          }
        }
        """;

    private const string JioReceipt = """
        {
          "type": "receipt",
          "category": "Bills and recharges",
          "title": "Jio recharge",
          "caption": "UPI payment of 599 INR to Jio for a mobile recharge",
          "text": "Payment successful ... Jio ... 599.00 ...",
          "receipt": { "vendor": "Jio", "date": "2026-10-20", "amount": 599, "currency": "INR", "payment_method": "upi" }
        }
        """;

    private static readonly Category[] Categories =
    [
        new(1, FileKind.Receipt, "Bills and recharges", "Electricity, mobile and internet bills", "Bills", false),
        new(2, FileKind.Receipt, "Other", "", "Other", true),
        new(3, FileKind.Screenshot, "Chats", "Messages from chat apps", "Chats", false),
        new(4, FileKind.Document, "Work", "Work documents", "Work", false)
    ];

    // Stands in for Ollama's POST /api/chat and records what SlothBox sent.
    private sealed class FakeOllama(Func<int, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public List<(string Path, JsonNode Body)> Requests { get; } = [];

        public static FakeOllama Answering(string content) => new((_, _) => Task.FromResult(Chat(content)));

        public static HttpResponseMessage Chat(string content) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                new JsonObject
                {
                    ["model"] = "gemma4:e4b",
                    ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
                    ["done"] = true
                }.ToJsonString(), Encoding.UTF8, "application/json")
        };

        public static HttpResponseMessage Raw(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.AbsolutePath,
                JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!));
            return await respond(Requests.Count, ct);
        }
    }

    private static FileAnalyzer Analyzer(FakeOllama ollama, TimeSpan? attemptTimeout = null) =>
        new(new HttpClient(ollama) { BaseAddress = new Uri("http://127.0.0.1:11436") }, new OllamaSettings(),
            new PipelineSettings(), attemptTimeout);

    private static AnalysisRequest Request(FileContent content) =>
        new("Screenshot_20261020-101500.jpg", "Downloads", content, Categories);

    private static JsonNode UserMessage(JsonNode body) => body["messages"]!.AsArray().Last()!;

    [Fact]
    public async Task An_image_is_sent_as_base64_on_the_user_message()
    {
        var ollama = FakeOllama.Answering(JioReceipt);
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

        await Analyzer(ollama).AnalyzeAsync(Request(new FileContent.Image(jpeg)), Ct);

        var (path, body) = ollama.Requests.ShouldHaveSingleItem();
        path.ShouldBe("/api/chat");
        var user = UserMessage(body);
        user["role"]!.GetValue<string>().ShouldBe("user");
        user["images"]!.AsArray().Select(i => i!.GetValue<string>()).ShouldBe([Convert.ToBase64String(jpeg)]);
        user["content"]!.GetValue<string>().ShouldEndWith("Content: the attached image");
    }

    [Fact]
    public async Task The_prompt_names_the_folder_the_file_and_every_category_by_type()
    {
        var ollama = FakeOllama.Answering(JioReceipt);

        await Analyzer(ollama).AnalyzeAsync(Request(new FileContent.Text("Payment successful. Jio 599.00")), Ct);

        var body = ollama.Requests.Single().Body;
        var messages = body["messages"]!.AsArray();
        messages.Count.ShouldBe(2);
        messages[0]!["role"]!.GetValue<string>().ShouldBe("system");
        messages[0]!["content"]!.GetValue<string>().ShouldStartWith("You are SlothBox's file reader.");
        UserMessage(body)["content"]!.GetValue<string>().ReplaceLineEndings("\n").ShouldBe("""
            Folder: Downloads
            Original file name: Screenshot_20261020-101500.jpg
            Categories:
              receipt: Bills and recharges — Electricity, mobile and internet bills; Other
              screenshot: Chats — Messages from chat apps
              document: Work — Work documents
            Content: Payment successful. Jio 599.00
            """.ReplaceLineEndings("\n"));
        UserMessage(body)["images"].ShouldBeNull();
    }

    [Fact]
    public async Task Text_sent_to_gemma_is_cut_to_the_first_6000_characters()
    {
        var ollama = FakeOllama.Answering(JioReceipt);

        await Analyzer(ollama).AnalyzeAsync(Request(new FileContent.Text(new string('a', 6000) + "TAIL")), Ct);

        var content = UserMessage(ollama.Requests.Single().Body)["content"]!.GetValue<string>();
        content.ShouldEndWith("Content: " + new string('a', 6000));
    }

    [Fact]
    public async Task The_schema_is_sent_as_a_json_object_not_a_string()
    {
        var ollama = FakeOllama.Answering(JioReceipt);

        await Analyzer(ollama).AnalyzeAsync(Request(new FileContent.Text("Jio 599")), Ct);

        var format = ollama.Requests.Single().Body["format"];
        format.ShouldBeOfType<JsonObject>();
        JsonNode.DeepEquals(format, JsonNode.Parse(SpecSchema)).ShouldBeTrue();
    }

    [Fact]
    public async Task Gemma_runs_without_thinking_at_temperature_0_with_a_fixed_context_and_stays_loaded()
    {
        var ollama = FakeOllama.Answering(JioReceipt);

        await Analyzer(ollama).AnalyzeAsync(Request(new FileContent.Text("Jio 599")), Ct);

        var body = ollama.Requests.Single().Body;
        body["model"]!.GetValue<string>().ShouldBe("gemma4:e4b");
        body["stream"]!.GetValue<bool>().ShouldBeFalse();
        body["think"]!.GetValue<bool>().ShouldBeFalse();
        body["keep_alive"]!.GetValue<string>().ShouldBe("30m");
        body["options"]!["num_ctx"]!.GetValue<int>().ShouldBe(8192);
        body["options"]!["temperature"]!.GetValue<double>().ShouldBe(0);
    }

    [Fact]
    public async Task A_receipt_answer_is_parsed_into_an_analysis()
    {
        var result = await Analyzer(FakeOllama.Answering(JioReceipt))
            .AnalyzeAsync(Request(new FileContent.Text("Jio 599")), Ct);

        result.ShouldBeOfType<AnalysisResult.Success>().Analysis.ShouldBe(new Analysis(FileKind.Receipt,
            "Bills and recharges", "Jio recharge", "UPI payment of 599 INR to Jio for a mobile recharge",
            "Payment successful ... Jio ... 599.00 ...",
            new ReceiptData("Jio", new DateOnly(2026, 10, 20), 599m, "INR", PaymentMethod.Upi)));
    }

    [Fact]
    public async Task A_category_copied_with_its_description_is_read_as_the_name()
    {
        // Seen on the first real call: the prompt lists "name — description" and Gemma copied both.
        var answer = JioReceipt.Replace("\"Bills and recharges\"",
            "\"Bills and recharges — Electricity, mobile and internet bills\"");

        var result = await Analyzer(FakeOllama.Answering(answer))
            .AnalyzeAsync(Request(new FileContent.Text("Jio 599")), Ct);

        result.ShouldBeOfType<AnalysisResult.Success>().Analysis.Category.ShouldBe("Bills and recharges");
    }

    [Fact]
    public async Task Nulls_in_the_answer_stay_null_and_a_missing_payment_method_is_unknown()
    {
        const string answer = """
            {"type":"receipt","category":"Other","title":"Shop bill","caption":"A bill.","text":"",
             "receipt":{"vendor":null,"date":null,"amount":null,"currency":null,"payment_method":null}}
            """;

        var result = await Analyzer(FakeOllama.Answering(answer))
            .AnalyzeAsync(Request(new FileContent.Text("bill")), Ct);

        result.ShouldBeOfType<AnalysisResult.Success>().Analysis.Receipt
            .ShouldBe(new ReceiptData(null, null, null, null, PaymentMethod.Unknown));
    }

    [Fact]
    public async Task Empty_strings_and_a_zero_amount_are_read_back_as_null()
    {
        // The fallback for an Ollama that refuses nullable types (spec 5.4).
        const string answer = """
            {"type":"receipt","category":"Other","title":"Shop bill","caption":"A bill.","text":"",
             "receipt":{"vendor":"","date":"","amount":0,"currency":"","payment_method":""}}
            """;

        var result = await Analyzer(FakeOllama.Answering(answer))
            .AnalyzeAsync(Request(new FileContent.Text("bill")), Ct);

        result.ShouldBeOfType<AnalysisResult.Success>().Analysis.Receipt
            .ShouldBe(new ReceiptData(null, null, null, null, PaymentMethod.Unknown));
    }

    [Fact]
    public async Task A_date_that_is_not_yyyy_mm_dd_is_treated_as_not_visible()
    {
        var answer = JioReceipt.Replace("2026-10-20", "20/10/2026");

        var result = await Analyzer(FakeOllama.Answering(answer))
            .AnalyzeAsync(Request(new FileContent.Text("Jio 599")), Ct);

        result.ShouldBeOfType<AnalysisResult.Success>().Analysis.Receipt!.Date.ShouldBeNull();
    }

    [Fact]
    public async Task A_screenshot_without_a_receipt_has_no_receipt_data()
    {
        const string answer = """
            {"type":"screenshot","category":"Chats","title":"Chat about dinner","caption":"A chat.",
             "text":"See you at 8","receipt":null}
            """;

        var result = await Analyzer(FakeOllama.Answering(answer))
            .AnalyzeAsync(Request(new FileContent.Text("chat")), Ct);

        var analysis = result.ShouldBeOfType<AnalysisResult.Success>().Analysis;
        analysis.Type.ShouldBe(FileKind.Screenshot);
        analysis.Receipt.ShouldBeNull();
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"type":"receipt","category":"Other","caption":"","text":"","receipt":null}""")]
    [InlineData("""{"type":"invoice","category":"Other","title":"","caption":"","text":"","receipt":null}""")]
    [InlineData("""{"type":"receipt","category":"Other","title":"","caption":"","text":"","receipt":{"amount":"599"}}""")]
    [InlineData("""{"type":"receipt","category":"Other","title":5,"caption":"","text":"","receipt":null}""")]
    [InlineData("""{"type":"receipt","category":"Other","title":"","caption":"","text":""}""")]
    [InlineData("""{"type":"receipt","category":"Other","title":"\ud83d","caption":"","text":"","receipt":null}""")]
    public async Task Output_that_does_not_match_the_schema_is_tried_once_more_then_needs_review(string answer)
    {
        var ollama = FakeOllama.Answering(answer);

        var result = await Analyzer(ollama).AnalyzeAsync(Request(new FileContent.Text("Jio 599")), Ct);

        result.ShouldBe(new AnalysisResult.NeedsReview(ReviewReason.BadOutput));
        ollama.Requests.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"message":"x"}""")]
    [InlineData("""{"message":{"content":5}}""")]
    [InlineData("""{"done":true}""")]
    public async Task An_ollama_reply_without_a_message_is_tried_once_more_then_needs_review(string reply)
    {
        var ollama = new FakeOllama((_, _) => Task.FromResult(FakeOllama.Raw(reply)));

        var result = await Analyzer(ollama).AnalyzeAsync(Request(new FileContent.Text("Jio 599")), Ct);

        result.ShouldBe(new AnalysisResult.NeedsReview(ReviewReason.BadOutput));
        ollama.Requests.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("inr", "INR")]
    [InlineData(" INR ", "INR")]
    [InlineData("Rupees", null)]
    [InlineData("₹", null)]
    public async Task A_currency_is_kept_only_as_a_3_letter_code(string currency, string? expected)
    {
        // The ledger column is char(3).
        var answer = JioReceipt.Replace("\"currency\": \"INR\"", $"\"currency\": \"{currency}\"");

        var result = await Analyzer(FakeOllama.Answering(answer))
            .AnalyzeAsync(Request(new FileContent.Text("Jio 599")), Ct);

        result.ShouldBeOfType<AnalysisResult.Success>().Analysis.Receipt!.Currency.ShouldBe(expected);
    }

    [Fact]
    public async Task An_amount_too_large_for_the_ledger_is_read_as_null()
    {
        // numeric(12,2) holds 10 digits before the point; a 12-digit UPI reference read as the amount does not fit.
        var answer = JioReceipt.Replace("\"amount\": 599", "\"amount\": 123456789012");

        var result = await Analyzer(FakeOllama.Answering(answer))
            .AnalyzeAsync(Request(new FileContent.Text("Jio 599")), Ct);

        result.ShouldBeOfType<AnalysisResult.Success>().Analysis.Receipt!.Amount.ShouldBeNull();
    }

    [Fact]
    public async Task Control_characters_in_the_answer_are_removed()
    {
        // Postgres text cannot hold NUL.
        var answer = JioReceipt.Replace("\"Jio recharge\"", "\"Jio\\u0000 recharge\"");

        var result = await Analyzer(FakeOllama.Answering(answer))
            .AnalyzeAsync(Request(new FileContent.Text("Jio 599")), Ct);

        result.ShouldBeOfType<AnalysisResult.Success>().Analysis.Title.ShouldBe("Jio recharge");
    }

    [Fact]
    public async Task A_category_name_that_contains_a_dash_is_kept_whole()
    {
        var answer = JioReceipt.Replace("\"Bills and recharges\"", "\"Bills — utilities\"");
        var request = Request(new FileContent.Text("Jio 599")) with
        {
            Categories = [.. Categories, new(5, FileKind.Receipt, "Bills — utilities", "", "Utilities", false)]
        };

        var result = await Analyzer(FakeOllama.Answering(answer)).AnalyzeAsync(request, Ct);

        result.ShouldBeOfType<AnalysisResult.Success>().Analysis.Category.ShouldBe("Bills — utilities");
    }

    [Fact]
    public async Task A_bad_answer_followed_by_a_good_one_is_filed()
    {
        var ollama = new FakeOllama((attempt, _) =>
            Task.FromResult(FakeOllama.Chat(attempt == 1 ? "{\"type\":" : JioReceipt)));

        var result = await Analyzer(ollama).AnalyzeAsync(Request(new FileContent.Text("Jio 599")), Ct);

        result.ShouldBeOfType<AnalysisResult.Success>();
        ollama.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_call_that_times_out_is_tried_once_more()
    {
        var ollama = new FakeOllama(async (attempt, ct) =>
        {
            if (attempt == 1)
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return FakeOllama.Chat(JioReceipt);
        });

        var result = await Analyzer(ollama, TimeSpan.FromMilliseconds(200))
            .AnalyzeAsync(Request(new FileContent.Text("Jio 599")), Ct);

        result.ShouldBeOfType<AnalysisResult.Success>();
        ollama.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_second_timeout_sends_the_file_to_review()
    {
        var ollama = new FakeOllama(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return FakeOllama.Chat(JioReceipt);
        });

        var result = await Analyzer(ollama, TimeSpan.FromMilliseconds(200))
            .AnalyzeAsync(Request(new FileContent.Text("Jio 599")), Ct);

        result.ShouldBe(new AnalysisResult.NeedsReview(ReviewReason.BadOutput));
        ollama.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Ollama_refusing_the_call_is_an_error_not_a_review()
    {
        // Section 9: Ollama down or out of memory pauses the queue; the file is not judged.
        var ollama = new FakeOllama((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        await Should.ThrowAsync<HttpRequestException>(() =>
            Analyzer(ollama).AnalyzeAsync(Request(new FileContent.Text("Jio 599")), Ct));
        ollama.Requests.Count.ShouldBe(1);
    }
}
