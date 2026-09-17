namespace Shop133.Web.Gateway;


public sealed record CatalogCategory
{
    public required int Id { get; init; }

    public required string Name { get; init; }

    
    public required int ProductCount { get; init; }
}
