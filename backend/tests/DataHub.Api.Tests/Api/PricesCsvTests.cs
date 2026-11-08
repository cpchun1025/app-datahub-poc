using System.Globalization;
using System.Text;
using DataHub.Api.Domain;
using DataHub.Api.Endpoints;

namespace DataHub.Api.Tests.Api;

public class PricesCsvTests
{
    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData("0000123", "\"0000123\"")]
    [InlineData("a,b\"c", "\"a,b\"\"c\"")]
    [InlineData("香港", "\"香港\"")]
    [InlineData("=SUM(A1)", "\"'=SUM(A1)\"")]
    [InlineData(" +1", "\"' +1\"")]
    [InlineData("-ticker", "\"'-ticker\"")]
    [InlineData("@command", "\"'@command\"")]
    [InlineData("\t=1", "\"'\t=1\"")]
    [InlineData("a\r\nb", "\"'a\r\nb\"")]
    [InlineData("\ufeff=1", "\"'\ufeff=1\"")]
    [InlineData(null, "\"\"")]
    public void EscapesTextAndNeutralizesFormulas(string? input, string expected)
        => Assert.Equal(expected, PricesCsv.Text(input));

    [Fact]
    public async Task EmitsBomCrLfInvariantNumbersAndUtc_NotFormulasForNegativePrices()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var time = DateTimeOffset.Parse("2026-01-02T08:00:00+08:00", CultureInfo.InvariantCulture);
            var row = new PriceDto("alpha", "0000123", -12.50m, "USD", time, time, 2, SourceStatus.Live);
            await using var stream = new MemoryStream();
            await PricesCsv.WriteAsync(stream, [row], CancellationToken.None);
            var bytes = stream.ToArray();
            Assert.Equal(Encoding.UTF8.GetPreamble(), bytes.Take(3));
            var csv = Encoding.UTF8.GetString(bytes);
            Assert.Contains("\"0000123\",-12.50,\"USD\",2026-01-02T00:00:00.0000000+00:00", csv);
            Assert.EndsWith(",2,\"Live\"\r\n", csv);
            Assert.DoesNotContain("=\"", csv);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task CancelledWriteStops()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        await using var stream = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PricesCsv.WriteAsync(stream, [], source.Token));
    }
}
