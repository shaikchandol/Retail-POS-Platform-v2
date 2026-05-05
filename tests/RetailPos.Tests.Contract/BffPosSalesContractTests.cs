using PactNet;
using PactNet.Matchers;
using System.Net.Http.Json;
using Xunit;

namespace RetailPos.Tests.Contract;

/// <summary>
/// Fix #8: Consumer-driven contract tests (Pact).
///
/// v1 Drawback: E2E tests were slow (30–60s per test), brittle (8 containers),
/// and tested too much at once. A failure in Kafka or Dapr could fail a
/// test that was actually validating HTTP routing logic.
///
/// Fix: Consumer-driven contract tests (Pact) between BFF and downstream services.
/// - POS BFF is the CONSUMER — defines what response shape it needs
/// - Sales Service is the PROVIDER — proves it can satisfy the contract
/// - Tests run without any real infrastructure (no containers, no Kafka)
/// - Contract file is published to a Pact Broker (CI/CD gate)
/// - Provider verification runs in the Sales service pipeline
///
/// Speed: < 1 second per contract test (vs 45s for E2E)
/// Reliability: 100% isolated — no flakiness from infrastructure
/// </summary>
public class BffPosSalesContractTests : IDisposable
{
    private readonly IPactBuilderV4 _pactBuilder;

    public BffPosSalesContractTests()
    {
        var pact = Pact.V4("POS-BFF", "Sales-Service", new PactConfig
        {
            PactDir    = Path.Combine(Directory.GetCurrentDirectory(), "pacts"),
            LogLevel   = PactLogLevel.Warning
        });
        _pactBuilder = pact.WithHttpInteractions();
    }

    // ── Contract 1: Create Sale ───────────────────────────────────────────────
    [Fact]
    [Trait("Category", "Contract")]
    public async Task CreateSale_Returns201_WithSaleIdAndReceiptNumber()
    {
        _pactBuilder
            .UponReceiving("a valid create sale request from POS BFF")
            .Given("the tenant 'acme' exists and is active")
            .WithRequest(method: HttpMethod.Post, path: "/api/v1/sales")
            .WithHeader("X-Tenant-Id", "acme")
            .WithHeader("X-Store-Id", "store-01")
            .WithJsonBody(new
            {
                customerId    = "customer-001",
                paymentMethod = "CARD",
                currency      = "USD",
                items         = new[]
                {
                    new { productId = Match.Type("prod-001"), productName = Match.Type("Widget"), sku = Match.Type("WGT-001"), quantity = 1, unitPrice = Match.Decimal(10.00), taxRate = Match.Decimal(0.1) }
                }
            })
            .WillRespond()
            .WithStatus(201)
            .WithHeader("Content-Type", "application/json")
            .WithJsonBody(new
            {
                saleId        = Match.Type("3fa85f64-5717-4562-b3fc-2c963f66afa6"),
                receiptNumber = Match.Regex("RCP-\\d{4}-\\d{6}", "RCP-2025-000001"),
                totalAmount   = Match.Decimal(11.00),
                currency      = "USD",
                taxTotal      = Match.Decimal(1.00)
            });

        await _pactBuilder.VerifyAsync(async ctx =>
        {
            var client = new HttpClient { BaseAddress = ctx.MockServerUri };
            client.DefaultRequestHeaders.Add("X-Tenant-Id", "acme");
            client.DefaultRequestHeaders.Add("X-Store-Id", "store-01");

            var response = await client.PostAsJsonAsync("/api/v1/sales", new
            {
                customerId    = "customer-001",
                paymentMethod = "CARD",
                currency      = "USD",
                items         = new[] { new { productId = "prod-001", productName = "Widget", sku = "WGT-001", quantity = 1, unitPrice = 10.00, taxRate = 0.1 } }
            });

            Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        });
    }

    // ── Contract 2: Get Sale ──────────────────────────────────────────────────
    [Fact]
    [Trait("Category", "Contract")]
    public async Task GetSale_ByValidId_Returns200_WithSaleDetails()
    {
        var saleId = "3fa85f64-5717-4562-b3fc-2c963f66afa6";

        _pactBuilder
            .UponReceiving("a get sale request from POS BFF")
            .Given($"a sale with id '{saleId}' exists for tenant 'acme'")
            .WithRequest(method: HttpMethod.Get, path: $"/api/v1/sales/{saleId}")
            .WithHeader("X-Tenant-Id", "acme")
            .WillRespond()
            .WithStatus(200)
            .WithJsonBody(new
            {
                saleId        = saleId,
                status        = Match.Regex("Active|Completed|Voided", "Completed"),
                receiptNumber = Match.Type("RCP-2025-000001"),
                totalAmount   = Match.Decimal(11.00),
                currency      = "USD",
                createdAt     = Match.Type("2025-01-01T00:00:00Z")
            });

        await _pactBuilder.VerifyAsync(async ctx =>
        {
            var client = new HttpClient { BaseAddress = ctx.MockServerUri };
            client.DefaultRequestHeaders.Add("X-Tenant-Id", "acme");

            var response = await client.GetAsync($"/api/v1/sales/{saleId}");
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        });
    }

    // ── Contract 3: Sale Not Found ────────────────────────────────────────────
    [Fact]
    [Trait("Category", "Contract")]
    public async Task GetSale_ByInvalidId_Returns404()
    {
        var nonExistentId = "00000000-0000-0000-0000-000000000000";

        _pactBuilder
            .UponReceiving("a get sale request for a non-existent sale from POS BFF")
            .Given("no sale exists with id '00000000-0000-0000-0000-000000000000'")
            .WithRequest(method: HttpMethod.Get, path: $"/api/v1/sales/{nonExistentId}")
            .WithHeader("X-Tenant-Id", "acme")
            .WillRespond()
            .WithStatus(404)
            .WithJsonBody(new { error = Match.Type("SALE_NOT_FOUND") });

        await _pactBuilder.VerifyAsync(async ctx =>
        {
            var client = new HttpClient { BaseAddress = ctx.MockServerUri };
            client.DefaultRequestHeaders.Add("X-Tenant-Id", "acme");

            var response = await client.GetAsync($"/api/v1/sales/{nonExistentId}");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        });
    }

    public void Dispose() { }
}

/// <summary>
/// Provider-side verification — runs in the Sales service pipeline.
/// Fetches the published contract from Pact Broker and verifies Sales service satisfies it.
/// </summary>
public class SalesServicePactProviderTests
{
    [Fact]
    [Trait("Category", "Contract")]
    public void SalesService_SatisfiesPossBffContract()
    {
        var pactBrokerUrl = Environment.GetEnvironmentVariable("PACT_BROKER_URL") ?? "http://localhost:9292";
        var serviceBaseUrl = Environment.GetEnvironmentVariable("SALES_SERVICE_URL") ?? "http://localhost:5001";

        var verifier = new PactVerifier("Sales-Service", new PactVerifierConfig());

        verifier
            .WithHttpEndpoint(new Uri(serviceBaseUrl))
            .WithPactBrokerSource(new Uri(pactBrokerUrl), options =>
            {
                options.ConsumerVersionSelectors([new ConsumerVersionSelector { MainBranch = true }]);
                options.PublishResults(true, "1.0.0");
            })
            .Verify();
    }
}
