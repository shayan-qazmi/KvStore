using KvStore.Core;
using Xunit;

namespace KvStore.Core.Tests;

public class KvStoreTests
{
    [Fact]
    public void Put_Then_Get_Returns_Value()
    {
        var store = new KvStore();
        store.Put("name", "shayan");

        Assert.Equal("shayan", store.Get("name"));
    }

    [Fact]
    public void Get_Missing_Key_Throws()
    {
        var store = new KvStore();

        Assert.Throws<KeyNotFoundInStoreException>(() => store.Get("missing"));
    }

    [Fact]
    public void Delete_Then_Get_Throws()
    {
        var store = new KvStore();
        store.Put("k", "v");
        store.Delete("k");

        Assert.Throws<KeyNotFoundInStoreException>(() => store.Get("k"));
    }

    [Fact]
    public void Overwriting_Key_Updates_Value()
    {
        var store = new KvStore();
        store.Put("k", "first");
        store.Put("k", "second");

        Assert.Equal("second", store.Get("k"));
    }

    [Fact]
    public void GetRange_Returns_Correct_Subset()
    {
        var store = new KvStore();
        store.Put("a", "1");
        store.Put("b", "2");
        store.Put("c", "3");
        store.Put("d", "4");

        var result = store.GetRange("b", "c").ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal("2", result[0].Value);
        Assert.Equal("3", result[1].Value);
    }
}