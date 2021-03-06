namespace PSIS 
{
	using System.Collections.Generic;
	using System;
	using System.Linq;
	using System.Threading;
	using System.Threading.Tasks;
	using System.Data.SqlClient;
	
	public static class PSS 
	{
		public static Dictionary<string, long> PopulateTrades () 
		{
			var cs = "Data Source=.;Integrated Security=True;Initial Catalog=DestSQLDatabase";
			Func<DestSQLDatabase> CreateDC = () => new DestSQLDatabase(cs);

			var newSymbolItems = new List<DW_DimSymbol>();
			var newSideItems = new List<DW_DimSide>();
			var SymbolKeyTask =Task.Factory.StartNew( SSRuntime.Using( CreateDC,
					(dc) => dc.DW_DimSymbol.Max(d=> (int?) d.SymbolKey).GetValueOrDefault(1) ));
			var SideKeyTask =Task.Factory.StartNew( SSRuntime.Using( CreateDC,
					(dc) => dc.DW_DimSide.Max(d=> (int?) d.SideKey).GetValueOrDefault(1) ));


			var SymbolDictTask = Task.Factory.StartNew(SSRuntime.Using( CreateDC,
					(dc) => SSRuntime.TryCatchThrow(
							() => dc.DW_DimSymbol.ToDictionary(d => new { d.SymbolValue, d.FullName }),
								(ArgumentException ex) => Console.WriteLine("Failed to create lookup for DW.DimSymbol",ex))));
			var SideDictTask = Task.Factory.StartNew(SSRuntime.Using( CreateDC,
					(dc) => SSRuntime.TryCatchThrow(
							() => dc.DW_DimSide.ToDictionary(d => new { d.SideValue }),
								(ArgumentException ex) => Console.WriteLine("Failed to create lookup for DW.DimSide",ex))));


			var SymbolKey		=	SymbolKeyTask.Result;
			var SideKey		=	SideKeyTask.Result;


			SymbolKeyTask.Dispose();	SymbolKeyTask=null;
			SideKeyTask.Dispose();	SideKeyTask=null;
			Func<Staging_TradesDWView,int> GetSymbolKey = (frow) => 
			{
				DW_DimSymbol dim;
				var lookup = new { SymbolValue = frow.Symbol_SymbolValue, FullName = frow.Symbol_FullName };
				var dict = SymbolDictTask.Result;
				lock(newSymbolItems)
					if (!dict.TryGetValue(lookup,out dim))
					{
						dim = new DW_DimSymbol() { SymbolValue = frow.Symbol_SymbolValue, FullName = frow.Symbol_FullName } ;
						dim.SymbolKey = Interlocked.Increment(ref SymbolKey);
						dict.Add(lookup,dim);
						newSymbolItems.Add(dim);
					}
				return dim.SymbolKey;
			};
			Func<Staging_TradesDWView,int> GetSideKey = (frow) => 
			{
				DW_DimSide dim;
				var lookup = new { SideValue = frow.Side_SideValue };
				var dict = SideDictTask.Result;
				lock(newSideItems)
					if (!dict.TryGetValue(lookup,out dim))
					{
						dim = new DW_DimSide() { SideValue = frow.Side_SideValue } ;
						dim.SideKey = Interlocked.Increment(ref SideKey);
						dict.Add(lookup,dim);
						newSideItems.Add(dim);
					}
				return dim.SideKey;
			};

			var restbl = new Dictionary<string, long>();	
			List<Task> tasks = new List<Task>();
			using (var dc = CreateDC())
			{
				var newFacts = dc.Staging_TradesDWView
                            .AsParallel()
                            .Select(src =>
                {
                    var fact = new DW_FactTrades();
					fact.ExecutionTimeKey = src.FACT_ExecutionTimeKey;
					fact.Price = src.FACT_Price;
					fact.Size = src.FACT_Size;
					fact.ExchangeFee = src.FACT_ExchangeFee;
					fact.BrokerFee = src.FACT_BrokerFee;
					fact.SECFee = src.FACT_SECFee;
					fact.MarginFee = src.FACT_MarginFee;
					fact.SymbolKey = GetSymbolKey(src);
					fact.SideKey = GetSideKey(src);
   
					return fact;
                });

				restbl["DW.FactTrades"] = PSIS.SSRuntime.SubmitChangesInBulk(newFacts, cs, "DW.FactTrades",SqlBulkCopyOptions.Default,15*60);
			}
			if (newSymbolItems.Any())
			{
				Task t = Task.Factory.StartNew(() => PSIS.SSRuntime.SubmitChangesInBulk(newSymbolItems, cs, "DW.DimSymbol",SqlBulkCopyOptions.KeepIdentity,15*60));
				tasks.Add(t);
				restbl["DW.DimSymbol"] = newSymbolItems.Count;
			}

			if (newSideItems.Any())
			{
				Task t = Task.Factory.StartNew(() => PSIS.SSRuntime.SubmitChangesInBulk(newSideItems, cs, "DW.DimSide",SqlBulkCopyOptions.KeepIdentity,15*60));
				tasks.Add(t);
				restbl["DW.DimSide"] = newSideItems.Count;
			}

			Task.WaitAll(tasks.ToArray());
			return restbl;
		}
		
	
	}
}

