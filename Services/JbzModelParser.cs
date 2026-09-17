using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

public sealed class JbzModelParser
{
    public ProductModel Load(string path) => JbzModelCompiler.Compile(path).Product;
    public IReadOnlyList<ProductModel> LoadAll(string path) => [Load(path)];
}
