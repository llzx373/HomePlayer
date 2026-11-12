using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public sealed class RenderRuleStoreTests
{
    [Fact]
    public void Store_round_trips_rules_and_reads_shader()
    {
        var directory = NewDirectory();
        try
        {
            var store = new RenderRuleStore(directory);
            Directory.CreateDirectory(store.ShaderDirectory);
            File.WriteAllText(Path.Combine(store.ShaderDirectory, "test.glsl"), "//!HOOK MAIN");
            store.Save(new RenderRulesDocument
            {
                Profiles = [new RenderProfileDefinition
                {
                    Id = "test",
                    Shaders = [new RenderShaderDefinition { File = "shaders/test.glsl" }]
                }]
            });

            var loaded = store.Load();

            Assert.Equal("test", Assert.Single(loaded.Document.Profiles).Id);
            Assert.Equal("//!HOOK MAIN", store.ReadShader("shaders/test.glsl"));
            Assert.False(string.IsNullOrWhiteSpace(loaded.Revision));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("../outside.glsl")]
    [InlineData("shaders/../../outside.glsl")]
    [InlineData("shaders/test.hlsl")]
    [InlineData("shaders/\0.glsl")]
    public void Store_rejects_paths_outside_root_and_unknown_extensions(string path)
    {
        var store = new RenderRuleStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Assert.Throws<InvalidDataException>(() => store.ResolveShaderPath(path));
    }

    [Fact]
    public void Import_does_not_overwrite_existing_shader()
    {
        var directory = NewDirectory();
        var source = Path.Combine(NewDirectory(), "test.glsl");
        try
        {
            File.WriteAllText(source, "first");
            var store = new RenderRuleStore(directory);

            var first = store.ImportShader(source);
            File.WriteAllText(source, "second");
            var second = store.ImportShader(source);

            Assert.Equal("shaders/test.glsl", first);
            Assert.Equal("shaders/test-2.glsl", second);
            Assert.Equal("first", store.ReadShader(first));
            Assert.Equal("second", store.ReadShader(second));
        }
        finally
        {
            Directory.Delete(directory, true);
            Directory.Delete(Path.GetDirectoryName(source)!, true);
        }
    }

    [Theory]
    [InlineData("{\"Profiles\":null}")]
    [InlineData("{\"Profiles\":[null]}")]
    [InlineData("{\"Profiles\":[{\"Id\":\"x\",\"Shaders\":null}]}")]
    public void Null_json_objects_are_rejected_without_crashing(string json)
    {
        var directory = NewDirectory();
        try
        {
            var store = new RenderRuleStore(directory);
            File.WriteAllText(store.DocumentPath, json);
            Assert.Throws<InvalidDataException>(() => store.Load());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "HomePlayer.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
