using System;
using System.Collections.Generic;
using Lineage;

namespace Lineage.Sample
{
    public static class SampleApp
    {
        public static void Run()
        {
            LogWarehouseSync();

            var catalog = GetCatalog();
            var product = catalog.Find(item => item.Sku == "WIDGET");
            var unitPrice = product.UnitPrice;
            Console.WriteLine(product.Name + " is $" + unitPrice + " each.");
            Console.Write("How many? ");
            var rawQuantity = Console.ReadLine();
            var quantity = ParseQuantity(rawQuantity);
            if (quantity <= 0)
            {
                throw new InvalidOperationException("Could not order '" + rawQuantity + "'");
            }

            var subtotal = LineTotal(unitPrice, quantity);
            var discountPercent = BulkDiscountPercent(quantity);
            var discount = ApplyDiscount(subtotal, discountPercent);
            var total = subtotal - discount;

            var name = product.Name;
            var item = name + " x";
            var counted = item + quantity;
            var gap = counted + " - ";
            var rated = gap + discountPercent;
            var suffix = rated + "% = $";
            var message = suffix + total;
            throw new InvalidOperationException(message);
        }

        public static string LogWarehouseSync()
        {
            return "sync-" + DateTime.UtcNow.Ticks;
        }

        public static List<Product> GetCatalog()
        {
            return new List<Product>
            {
                new Product { Sku = "GADGET", Name = "Gadget", UnitPrice = 12 },
                new Product { Sku = "WIDGET", Name = "Widget", UnitPrice = 20 },
                new Product { Sku = "CABLE", Name = "Cable", UnitPrice = 8 }
            };
        }

        public static int ParseQuantity(string text)
        {
            if (text == null || text.Length == 0)
            {
                return 0;
            }

            int value;
            return int.TryParse(text, out value) ? value : 0;
        }

        public static int LineTotal(int unitPrice, int quantity)
        {
            return unitPrice * quantity;
        }

        public static int BulkDiscountPercent(int quantity)
        {
            var percent = (quantity / 5) * 10;
            return percent > 50 ? 50 : percent;
        }

        public static int ApplyDiscount(int subtotal, int percent)
        {
            return subtotal * percent / 100;
        }
    }
}
