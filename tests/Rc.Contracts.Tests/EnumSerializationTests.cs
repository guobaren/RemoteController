using System.Text.Json;
using Xunit;

namespace Rc.Contracts.Tests;

public sealed class EnumSerializationTests
{
    [Fact]
    public void NumericEnumValuesAreRejectedOnDeserialization()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<JobState>("3", ContractJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ErrorCode>("6", ContractJson.Options));
    }
}
