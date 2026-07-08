using System;
using System.Reflection;
using System.Linq;

class Program {
    static void Main() {
        var asm = Assembly.LoadFrom(@"C:\Quantower\TradingPlatform\v1.146.13\bin\TradingPlatform.BusinessLayer.dll");
        
        var tLevel2 = asm.GetType("TradingPlatform.BusinessLayer.Level2Quote");
        if (tLevel2 != null) {
            Console.WriteLine("Level2Quote.PriceType type: " + tLevel2.GetProperty("PriceType").PropertyType.FullName);
        }
        
        var tOrder = asm.GetType("TradingPlatform.BusinessLayer.Order");
        if (tOrder != null) {
            Console.WriteLine("Order properties: " + string.Join(", ", tOrder.GetProperties().Select(p => p.Name + " (" + p.PropertyType.Name + ")")));
        }
        
        var tPos = asm.GetType("TradingPlatform.BusinessLayer.Position");
        if (tPos != null) {
            Console.WriteLine("Position properties: " + string.Join(", ", tPos.GetProperties().Select(p => p.Name + " (" + p.PropertyType.Name + ")")));
        }
    }
}
