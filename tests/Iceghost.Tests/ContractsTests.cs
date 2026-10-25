using System.Text.Json;
using Iceghost.Contracts;

namespace Iceghost.Tests;

public class ContractsTests
{
    [Fact]
    public void Enum_serializes_camel()
    {
        Assert.Equal("\"needsInput\"", JsonSerializer.Serialize(SessionState.NeedsInput, IceJson.Options));
    }

    [Fact]
    public void Heartbeat_roundtrips()
    {
        Assert.Equal("{\"hostname\":\"h\",\"os\":\"linux\",\"sessions\":null}",
            JsonSerializer.Serialize(new RunnerReport("h", "linux", null), IceJson.Options));
    }
}
