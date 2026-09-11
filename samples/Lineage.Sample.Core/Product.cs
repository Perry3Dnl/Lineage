namespace Lineage.Sample
{
    public sealed class Product
    {
        public string Sku { get; set; }
        public string Name { get; set; }
        public int UnitPrice { get; set; }

        public Product()
        {
            Sku = string.Empty;
            Name = string.Empty;
        }
    }
}
