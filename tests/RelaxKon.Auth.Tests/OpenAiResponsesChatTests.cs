using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKon.Auth.Application.Chat;
using RelaxKon.Auth.Application.Routing;
using RelaxKon.Auth.Contracts.Files;
using RelaxKon.Auth.Controllers.Files;
using RelaxKon.Auth.Models.Suppliers;

namespace RelaxKon.Auth.Tests;

public sealed class OpenAiResponsesChatTests
{
    private sealed class StubExtractor : IFileContentExtractor
    {
        public int Calls { get; private set; }
        public string Extractor(FileExtractionRequest file) { Calls++; return "extracted"; }
    }

    private sealed class Transport : HttpMessageHandler, IHttpClientFactory
    {
        public List<string> Paths { get; } = [];
        public string? Body { get; private set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Assert.That(request.Headers.Authorization?.Parameter, Is.EqualTo("fixture-key"));
            if (request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = new StringContent("x,y\n1,2") };

            Body = await request.Content!.ReadAsStringAsync(token);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    data: {"type":"response.output_text.delta","delta":"Done"}

                    data: {"type":"response.output_item.done","item":{"type":"image_generation_call","result":"aGVsbG8="}}

                    data: {"type":"response.completed","response":{"output":[{"type":"message","content":[{"type":"output_text","annotations":[{"type":"container_file_citation","file_id":"cfile_fixture","container_id":"cntr_fixture","filename":"result.csv"}]}]}]}}

                    """ + "\n", Encoding.UTF8, "text/event-stream")
            };
        }
    }

    [Test]
    public async Task Native_pdf_and_typed_outputs_follow_responses_wire_format()
    {
        using var transport = new Transport();
        var handler = new OpenAiResponsesHandler(transport, new ConfigurationBuilder().Build(),
            NullLogger<OpenAiResponsesHandler>.Instance);
        var route = new ModelKey { Model = "gpt-6.1-sol", SupplierKey = new SupplierKey { ApiKey = "fixture-key" } };
        var extractor = new StubExtractor();
        var input = new ChatAttachmentInput(extractor);
        var request = await input.Prepare(PdfRequest(true), true, CancellationToken.None);
        var outputs = new List<ChatOutput>();
        await foreach (var output in handler.Stream(request, route, CancellationToken.None)) outputs.Add(output);

        Assert.That(extractor.Calls, Is.Zero);
        Assert.That(transport.Paths, Is.EqualTo(new[] { "/v1/responses", "/v1/containers/cntr_fixture/files/cfile_fixture/content" }));
        using var body = JsonDocument.Parse(transport.Body!);
        var payload = body.RootElement;
        var file = payload.GetProperty("input")[0].GetProperty("content")[0];
        Assert.That(file.GetProperty("type").GetString(), Is.EqualTo("input_file"));
        Assert.That(file.GetProperty("file_data").GetString(), Does.StartWith("data:application/pdf;base64,"));
        Assert.That(payload.GetProperty("tools").EnumerateArray().Select(item => item.GetProperty("type").GetString()),
            Is.EquivalentTo(new[] { "image_generation", "code_interpreter" }));
        Assert.That(outputs[0].Text, Is.EqualTo("Done"));
        Assert.That(outputs[1].Attachment?.FileType, Is.EqualTo("image/png"));
        Assert.That(outputs[2].Attachment?.FileName, Is.EqualTo("result.csv"));
        Assert.That(Encoding.UTF8.GetString(Convert.FromBase64String(outputs[2].Attachment!.FileContent)), Is.EqualTo("x,y\n1,2"));
    }

    [Test]
    public async Task Pdf_on_other_suppliers_uses_existing_text_extractor()
    {
        var extractor = new StubExtractor();
        var result = await new ChatAttachmentInput(extractor).Prepare(PdfRequest(false), false, CancellationToken.None);
        Assert.That(extractor.Calls, Is.EqualTo(1));
        Assert.That(result.Messages[0].Content.PlainText, Does.Contain("extracted"));
    }

    [Test]
    public void Native_pdf_and_images_are_rejected_when_vision_is_disabled()
    {
        var input = new ChatAttachmentInput(new StubExtractor());
        var error = Assert.ThrowsAsync<ChatAttachmentException>(() => input.Prepare(PdfRequest(false), true, CancellationToken.None));
        Assert.That(error!.Code, Is.EqualTo("chat_pdf_unsupported"));
        var image = new ChatRequest
        {
            Messages = [new Message { Content = new(null, [new(ImageUrl: "data:image/png;base64,aA==")]) }]
        };
        error = Assert.ThrowsAsync<ChatAttachmentException>(() => input.Prepare(image, false, CancellationToken.None));
        Assert.That(error!.Code, Is.EqualTo("chat_image_unsupported"));
    }

    private static ChatRequest PdfRequest(bool vision) => new()
    {
        Vision = vision,
        Messages = [new Message { Content = new(null, [new(Attachment: new("test.pdf", "application/pdf",
            Convert.ToBase64String("%PDF-1.7"u8.ToArray())))]) }]
    };
}
