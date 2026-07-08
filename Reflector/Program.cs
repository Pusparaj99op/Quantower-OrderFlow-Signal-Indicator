using System;
using System.Reflection;
using System.Linq;

class Program {
    static void Main() {
        var asm = Assembly.LoadFrom(@"C:\Quantower\TradingPlatform\v1.146.14\bin\TradingPlatform.BusinessLayer.dll");

        // MessageType
        foreach (var t in asm.GetTypes().Where(tt => tt.Name == "MessageType" && tt.IsEnum)) {
            Console.WriteLine($"=== {t.FullName} ===");
            foreach (var v in Enum.GetNames(t)) Console.WriteLine($"  {v}");
        }
        
        // QuotePriceType
        foreach (var t in asm.GetTypes().Where(tt => tt.Name == "QuotePriceType" && tt.IsEnum)) {
            Console.WriteLine($"\n=== {t.FullName} ===");
            foreach (var v in Enum.GetNames(t)) Console.WriteLine($"  {v}");
        }
        
        // Strategy Log method 
        var tStrat = asm.GetType("TradingPlatform.BusinessLayer.Strategy");
        if (tStrat != null) {
            Console.WriteLine("\n=== Strategy ALL methods (names only) ===");
            foreach (var m in tStrat.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Where(mm => mm.DeclaringType == tStrat)
                .OrderBy(mm => mm.Name)) {
                var vis = m.IsPublic ? "public" : m.IsFamily ? "protected" : "private";
                Console.WriteLine($"  {vis} {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
            }
        }
        
        // Indicator ALL declared methods 
        var tInd = asm.GetType("TradingPlatform.BusinessLayer.Indicator");
        if (tInd != null) {
            Console.WriteLine("\n=== Indicator ALL declared methods ===");
            foreach (var m in tInd.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Where(mm => mm.DeclaringType == tInd)
                .OrderBy(mm => mm.Name)) {
                var vis = m.IsPublic ? "public" : m.IsFamily ? "protected" : "private";
                Console.WriteLine($"  {vis} {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
            }
        }
        
        // Check for Order.Core or Account order-related
        var tCore = asm.GetType("TradingPlatform.BusinessLayer.Core");
        if (tCore != null) {
            Console.WriteLine("\n=== Core Static Methods (Order/Position related) ===");
            foreach (var m in tCore.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(mm => mm.Name.Contains("Order") || mm.Name.Contains("Position") || mm.Name.Contains("Place"))
                .OrderBy(mm => mm.Name)) {
                Console.WriteLine($"  {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
            }
        }
    }
}
