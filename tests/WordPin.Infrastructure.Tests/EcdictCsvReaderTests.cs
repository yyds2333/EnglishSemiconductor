using System.Text;
using WordPin.Infrastructure.Dictionary;

namespace WordPin.Infrastructure.Tests;

public sealed class EcdictCsvReaderTests
{
    [Fact]
    public async Task ReadsHeaderByNameAndPreservesQuotedComma()
    {
        const string csv = "translation,word,definition,phonetic,pos,exchange\n\"学习,掌握\",learn,\"to learn, acquire\",/lɜːrn/,v,learned;learning\n";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var entries = new List<WordPin.Domain.DictionaryEntry>();
        await foreach (var entry in EcdictCsvReader.ReadAsync(stream, "test-1"))
        {
            entries.Add(entry);
        }

        var result = Assert.Single(entries);
        Assert.Equal("learn", result.Term);
        Assert.Equal("学习,掌握", result.Translation);
        Assert.Equal("to learn, acquire", result.Definition);
        Assert.Equal("learned;learning", result.WordForms);
        Assert.Equal("test-1", result.ProviderVersion);
    }

    [Fact]
    public void RejectsUnterminatedQuotedField()
    {
        var exception = Assert.Throws<InvalidDataException>(() => EcdictCsvReader.ParseCsvLine("word,\"broken"));

        Assert.Contains("unterminated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
