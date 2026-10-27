using RamMx.Core;
using RamMx.Persistence;
using Xunit;

namespace RamMx.Tests;

public sealed class SettingsEdgeTests
{
    [Fact] public void Null_profile_list_import_becomes_an_empty_candidate_set()
    {
        var settings = StateStore.Import("{\"ProfileCandidates\":{\"ทั่วไป\":null}}");
        Assert.Empty(settings.ProfileCandidates["ทั่วไป"]);
    }
    [Fact] public void Imported_candidate_keys_keep_case_insensitive_identity_matching()
    {
        var settings = StateStore.Import("{\"ProfileCandidates\":{\"ทั่วไป\":[\"file:EXAMPLE\"]}}");
        Assert.Contains("file:example", settings.ProfileCandidates["ทั่วไป"]);
    }
}
