﻿/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Moq;
using NodaTime;
using NUnit.Framework;
using QuantConnect.Algorithm;
using QuantConnect.Data;
using QuantConnect.Data.Auxiliary;
using QuantConnect.Data.Custom;
using QuantConnect.Data.Fundamental;
using QuantConnect.Data.Market;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Lean.Engine.Results;
using QuantConnect.Lean.Engine.TransactionHandlers;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Packets;
using QuantConnect.Securities;
using QuantConnect.Tests.Common.Securities;
using QuantConnect.Util;

namespace QuantConnect.Tests.Engine.DataFeeds
{
    [TestFixture]
    public class LiveTradingDataFeedTests
    {
        private static bool LogsEnabled = false; // this is for travis log no to fill up and reach the max size.
        private ManualTimeProvider _manualTimeProvider;
        private AlgorithmStub _algorithm;
        private LiveSynchronizer _synchronizer;
        private readonly DateTime _startDate = new DateTime(2018, 08, 1, 11, 0, 0);
        private DataManager _dataManager;

        [SetUp]
        public void SetUp()
        {
            CustomMockedFileBaseData.StartDate = _startDate;
            _manualTimeProvider = new ManualTimeProvider();
            _manualTimeProvider.SetCurrentTimeUtc(_startDate);
            _algorithm = new AlgorithmStub(false);
            TestCustomData.ReaderCallsCount = 0;
            TestCustomData.ReturnNull = false;
            TestCustomData.ThrowException = false;
        }

        [Test]
        public void EmitsData()
        {
            var feed = RunDataFeed(forex: new List<string> { Symbols.EURUSD });

            var emittedData = false;
            ConsumeBridge(feed, TimeSpan.FromSeconds(5), true, ts =>
            {
                if (ts.Slice.HasData)
                {
                    emittedData = true;
                    var data = ts.Slice[Symbols.EURUSD];
                    ConsoleWriteLine("HasData: " + data);
                    ConsoleWriteLine();
                }
            });

            Assert.IsTrue(emittedData);
        }

        [Test]
        public void HandlesMultipleSecurities()
        {
            var equities = new List<string> { "SPY", "IBM", "AAPL", "GOOG", "MSFT", "BAC", "GS" };
            var forex = new List<string> { "EURUSD", "USDJPY", "GBPJPY", "AUDUSD", "NZDUSD" };

            var feed = RunDataFeed(equities: equities, forex: forex);

            var emittedData = false;
            ConsumeBridge(feed, TimeSpan.FromSeconds(2), ts =>
            {
                var delta = (DateTime.UtcNow - ts.Time).TotalMilliseconds;
                var values = ts.Slice.Keys.Select(x => x.Value).ToList();
                ConsoleWriteLine(((decimal)delta).SmartRounding() + "ms : " + string.Join(",", values));
                Assert.IsTrue(equities.All(x => values.Contains(x)));
                Assert.IsTrue(forex.All(x => values.Contains(x)));
                emittedData = true;
            });
            Assert.IsTrue(emittedData);
        }

        [Test]
        public void PerformanceBenchmark()
        {
            var symbolCount = 600;

            FuncDataQueueHandler queue;
            var count = new Count();
            var stopwatch = Stopwatch.StartNew();
            var feed = RunDataFeed(out queue, fdqh => ProduceBenchmarkTicks(fdqh, count), Resolution.Tick,
                equities: Enumerable.Range(0, symbolCount).Select(x => "E" + x.ToString()).ToList());

            var securitiesCount = _algorithm.Securities.Count;
            var expected = _algorithm.Securities.Keys.ToHashSet();
            Console.WriteLine("Securities.Count: " + securitiesCount);

            ConsumeBridge(feed, TimeSpan.FromSeconds(5), ts =>
            {
                ConsoleWriteLine("Count: " + ts.Slice.Keys.Count + " " + DateTime.UtcNow.ToString("o"));
                if (ts.Slice.Keys.Count != securitiesCount)
                {
                    var included = ts.Slice.Keys.ToHashSet();
                    expected.ExceptWith(included);
                    ConsoleWriteLine("Missing: " + string.Join(",", expected.OrderBy(x => x.Value)));
                }
            });
            stopwatch.Stop();

            Console.WriteLine("Total ticks: " + count.Value);
            Assert.GreaterOrEqual(count.Value, 700000);
            Console.WriteLine("Elapsed time: " + stopwatch.Elapsed);
            var ticksPerSec = count.Value / stopwatch.Elapsed.TotalSeconds;
            Console.WriteLine("Ticks/sec: " + ticksPerSec);
            Assert.GreaterOrEqual(ticksPerSec, 70000);
            var ticksPerSecPerSymbol = (count.Value / stopwatch.Elapsed.TotalSeconds) / symbolCount;
            Console.WriteLine("Ticks/sec/symbol: " + ticksPerSecPerSymbol);
            Assert.GreaterOrEqual(ticksPerSecPerSymbol, 100);
        }

        [Test]
        public void DoesNotSubscribeToCustomData()
        {
            // Current implementation only sends equity/forex subscriptions to the queue handler,
            // new impl sends all, the restriction shouldn't live in the feed, but rather in the
            // queue handler impl
            FuncDataQueueHandler dataQueueHandler;
            var feed = RunDataFeed(out dataQueueHandler, equities: new List<string> { "SPY" }, forex: new List<string> { "EURUSD" });
            _algorithm.AddData<CustomMockedFileBaseData>("CustomMockedFileBaseData");
            var customMockedFileBaseData = SymbolCache.GetSymbol("CustomMockedFileBaseData");

            var emittedData = false;
            ConsumeBridge(feed, TimeSpan.FromSeconds(2), ts =>
            {
                ConsoleWriteLine("Count: " + ts.Slice.Keys.Count + " " + DateTime.UtcNow.ToString("o"));
                Assert.IsTrue(dataQueueHandler.Subscriptions.Contains(Symbols.SPY));
                Assert.IsTrue(dataQueueHandler.Subscriptions.Contains(Symbols.EURUSD));
                Assert.IsFalse(dataQueueHandler.Subscriptions.Contains(customMockedFileBaseData));
                emittedData = true;
            });

            Assert.IsTrue(emittedData);
        }

        [Test]
        public void AddsSubscription_NewUserUniverse()
        {
            FuncDataQueueHandler dataQueueHandler;
            var feed = RunDataFeed(out dataQueueHandler, equities: new List<string> { "SPY" });

            var forexFxcmUserUniverse = UserDefinedUniverse.CreateSymbol(SecurityType.Forex, Market.FXCM);
            var emittedData = false;
            var newDataCount = 0;
            var securityChanges = 0;
            ConsumeBridge(feed, TimeSpan.FromSeconds(5), true, ts =>
            {
                securityChanges += ts.SecurityChanges.Count;
                if (!emittedData)
                {
                    Assert.IsTrue(dataQueueHandler.Subscriptions.Contains(Symbols.SPY));
                    if (ts.Data.Count > 0)
                    {
                        Assert.IsTrue(ts.Slice.Keys.Contains(Symbols.SPY));
                    }
                    Assert.AreEqual(1, dataQueueHandler.Subscriptions.Count);

                    _algorithm.AddSecurities(forex: new List<string> { "EURUSD" });
                    emittedData = true;
                }
                else
                {
                    if (dataQueueHandler.Subscriptions.Count == 2) // there could be some slices with no data
                    {
                        Assert.IsTrue(dataQueueHandler.Subscriptions.Contains(Symbols.SPY));
                        if (ts.Data.Count > 0)
                        {
                            Assert.IsTrue(ts.Slice.Keys.Contains(Symbols.SPY));
                        }
                        Assert.IsTrue(dataQueueHandler.Subscriptions.Contains(Symbols.EURUSD)
                                      || dataQueueHandler.Subscriptions.Contains(forexFxcmUserUniverse));
                        // Might delay a couple of Slices to send over the data, so we will count them
                        // and assert a minimum amount
                        if (ts.Slice.Keys.Contains(Symbols.EURUSD))
                        {
                            newDataCount++;
                        }
                    }
                    else
                    {
                        Assert.Fail($"Subscriptions.Count: {dataQueueHandler.Subscriptions.Count}");
                    }
                }
            });

            Console.WriteLine("newDataCount: " + newDataCount);
            Assert.AreEqual(2, securityChanges);

            Assert.GreaterOrEqual(newDataCount, 5);
            Assert.IsTrue(emittedData);
        }

        [Test]
        public void AddsNewUniverse()
        {
            _algorithm.UniverseSettings.Resolution = Resolution.Second; // Default is Minute and we need something faster
            _algorithm.UniverseSettings.ExtendedMarketHours = true; // Current _startDate is at extended market hours

            FuncDataQueueHandler dataQueueHandler;
            var feed = RunDataFeed(out dataQueueHandler, forex: new List<string> { "EURUSD" });
            var firstTime = false;
            var securityChanges = 0;
            var newDataCount = 0;
            ConsumeBridge(feed, TimeSpan.FromSeconds(5), true, ts =>
            {
                securityChanges += ts.SecurityChanges.Count;
                if (!firstTime)
                {
                    Assert.AreEqual(1, dataQueueHandler.Subscriptions.Count);
                    _algorithm.AddUniverse("TestUniverse", time => new List<string> { "AAPL", "SPY" });
                    firstTime = true;
                }
                else
                {
                    if (dataQueueHandler.Subscriptions.Count == 2)
                    {
                        Assert.AreEqual(1, dataQueueHandler.Subscriptions.Count(x => x.Value.Contains("TESTUNIVERSE")));
                    }
                    else if(dataQueueHandler.Subscriptions.Count == 4)
                    {
                        Assert.AreEqual(1, dataQueueHandler.Subscriptions.Count(x => x.Value.Contains("TESTUNIVERSE")));
                        Assert.IsTrue(dataQueueHandler.Subscriptions.Contains(Symbols.SPY));
                        Assert.IsTrue(dataQueueHandler.Subscriptions.Contains(Symbols.AAPL));
                        Assert.IsTrue(dataQueueHandler.Subscriptions.Contains(Symbols.EURUSD));
                        // Might delay a couple of Slices to send over the data, so we will count them and assert a minimum amount
                        if (ts.Slice.Keys.Contains(Symbols.AAPL)
                            && ts.Slice.Keys.Contains(Symbols.SPY))
                        {
                            newDataCount++;
                        }
                    }
                    else
                    {
                        Assert.Fail($"Subscriptions.Count: {dataQueueHandler.Subscriptions.Count}");
                    }
                }
            });

            Console.WriteLine("newDataCount: " + newDataCount);
            Assert.AreEqual(3, securityChanges);

            Assert.GreaterOrEqual(newDataCount, 5);
            Assert.IsTrue(firstTime);
        }

        [Test]
        public void AddsSubscription_SameUserUniverse()
        {
            FuncDataQueueHandler dataQueueHandler;
            var feed = RunDataFeed(out dataQueueHandler, equities: new List<string> { "SPY" });

            var emittedData = false;
            var newDataCount = 0;
            var securityChanges = 0;
            ConsumeBridge(feed, TimeSpan.FromSeconds(5), true, ts =>
            {
                securityChanges += ts.SecurityChanges.Count;
                if (!emittedData)
                {
                    Assert.IsTrue(dataQueueHandler.Subscriptions.Contains(Symbols.SPY));
                    if (ts.Data.Count > 0)
                    {
                        Assert.IsTrue(ts.Slice.Keys.Contains(Symbols.SPY));
                    }
                    Assert.AreEqual(1, dataQueueHandler.Subscriptions.Count);

                    _algorithm.AddSecurities(equities: new List<string> { "AAPL" });
                    emittedData = true;
                }
                else
                {
                    if (dataQueueHandler.Subscriptions.Count == 2) // there could be some slices with no data
                    {
                        Assert.IsTrue(dataQueueHandler.Subscriptions.Contains(Symbols.SPY));
                        if (ts.Data.Count > 0)
                        {
                            Assert.IsTrue(ts.Slice.Keys.Contains(Symbols.SPY));
                        }
                        Assert.IsTrue(dataQueueHandler.Subscriptions.Contains(Symbols.AAPL));
                        // Might delay a couple of Slices to send over the data, so we will count them
                        // and assert a minimum amount
                        if (ts.Slice.Keys.Contains(Symbols.AAPL))
                        {
                            newDataCount++;
                        }
                    }
                    else
                    {
                        Assert.Fail($"Subscriptions.Count: {dataQueueHandler.Subscriptions.Count}");
                    }
                }
            });

            Assert.GreaterOrEqual(newDataCount, 5);
            Assert.IsTrue(emittedData);
            Assert.AreEqual(2, securityChanges + _algorithm.SecurityChangesRecord.Count);
            Assert.AreEqual(Symbols.AAPL, _algorithm.SecurityChangesRecord.First().AddedSecurities.First().Symbol);
        }

        [Test]
        public void Unsubscribes()
        {
            FuncDataQueueHandler dataQueueHandler;
            var feed = RunDataFeed(out dataQueueHandler, equities: new List<string> { "SPY" }, forex: new List<string> { "EURUSD" });
            _algorithm.AddData<CustomMockedFileBaseData>("CustomMockedFileBaseData");
            var customMockedFileBaseData = SymbolCache.GetSymbol("CustomMockedFileBaseData");

            var emittedData = false;
            var currentSubscriptionCount = 0;
            ConsumeBridge(feed, TimeSpan.FromSeconds(5), false, ts =>
            {
                Assert.IsFalse(dataQueueHandler.Subscriptions.Contains(customMockedFileBaseData));
                if (!emittedData)
                {
                    currentSubscriptionCount = dataQueueHandler.Subscriptions.Count;
                    Assert.IsTrue(dataQueueHandler.Subscriptions.Contains(Symbols.SPY));
                    Assert.IsTrue(dataQueueHandler.Subscriptions.Contains(Symbols.EURUSD));
                    _dataManager.RemoveSubscription(_dataManager.DataFeedSubscriptions
                        .Single(sub => sub.Configuration.Symbol == Symbols.SPY).Configuration);
                    emittedData = true;
                }
                else
                {
                    Assert.AreEqual(currentSubscriptionCount - 1, dataQueueHandler.Subscriptions.Count);
                    Assert.IsFalse(dataQueueHandler.Subscriptions.Contains(Symbols.SPY));
                    Assert.IsTrue(dataQueueHandler.Subscriptions.Contains(Symbols.EURUSD));
                }
            });

            Assert.IsTrue(emittedData);
        }

        [Test]
        public void RemoveSecurity()
        {
            _algorithm.SetFinishedWarmingUp();
            _algorithm.Transactions.SetOrderProcessor(new FakeOrderProcessor());
            FuncDataQueueHandler dataQueueHandler;
            var feed = RunDataFeed(out dataQueueHandler, equities: new List<string> { "SPY" }, forex: new List<string> { "EURUSD" });
            _algorithm.AddData<CustomMockedFileBaseData>("CustomMockedFileBaseData");
            var customMockedFileBaseData = SymbolCache.GetSymbol("CustomMockedFileBaseData");

            var emittedData = false;
            var currentSubscriptionCount = 0;
            var securityChanges = 0;
            ConsumeBridge(feed, TimeSpan.FromSeconds(5), true, ts =>
            {
                securityChanges += ts.SecurityChanges.Count;
                Assert.IsFalse(dataQueueHandler.Subscriptions.Contains(customMockedFileBaseData));
                if (!emittedData)
                {
                    currentSubscriptionCount = dataQueueHandler.Subscriptions.Count;
                    Assert.IsTrue(dataQueueHandler.Subscriptions.Contains(Symbols.SPY));
                    Assert.IsTrue(dataQueueHandler.Subscriptions.Contains(Symbols.EURUSD));
                    _algorithm.RemoveSecurity(Symbols.SPY);
                    emittedData = true;
                }
                else
                {
                    Assert.AreEqual(currentSubscriptionCount - 1, dataQueueHandler.Subscriptions.Count);
                    Assert.IsFalse(dataQueueHandler.Subscriptions.Contains(Symbols.SPY));
                    Assert.IsTrue(dataQueueHandler.Subscriptions.Contains(Symbols.EURUSD));
                }
            });

            Assert.IsTrue(emittedData);
            Assert.AreEqual(4, securityChanges + _algorithm.SecurityChangesRecord.Count);
            Assert.AreEqual(Symbols.SPY, _algorithm.SecurityChangesRecord.First().RemovedSecurities.First().Symbol);
        }

        [Test]
        public void BenchmarkTicksPerSecondWithTwentySymbols()
        {
            // this ran at ~25k ticks/per symbol for 20 symbols

            var feed = RunDataFeed(Resolution.Tick, equities: Enumerable.Range(0, 20).Select(x => x.ToString()).ToList());
            int ticks = 0;
            var averages = new List<decimal>();
            var timer = new Timer(state =>
            {
                var avg = ticks / 20m;
                Interlocked.Exchange(ref ticks, 0);
                Console.WriteLine("Average ticks per symbol: " + avg.SmartRounding());
                averages.Add(avg);
            }, null, Time.OneSecond, Time.OneSecond);

            ConsumeBridge(feed, TimeSpan.FromSeconds(5), false, ts =>
            {
                Interlocked.Add(ref ticks, ts.Slice.Ticks.Sum(x => x.Value.Count));
            });

            timer.Dispose();
            var average = averages.Average();
            Console.WriteLine("\r\nAverage ticks per symbol per second: " + average);
            Assert.That(average, Is.GreaterThan(40));
        }

        [Test]
        public void EmitsForexDataWithRoundedUtcTimes()
        {
            var feed = RunDataFeed(forex: new List<string> { "EURUSD" });

            var emittedData = false;
            var lastTime = DateTime.UtcNow;
            ConsumeBridge(feed, TimeSpan.FromSeconds(5), ts =>
            {
                if (!emittedData)
                {
                    emittedData = true;
                    lastTime = ts.Time;
                    return;
                }
                var delta = (DateTime.UtcNow - ts.Time).TotalMilliseconds;
                Assert.AreEqual(lastTime.Add(Time.OneSecond), ts.Time);
                Assert.AreEqual(1, ts.Slice.QuoteBars.Count);
                lastTime = ts.Time;
            });

            Assert.IsTrue(emittedData);
        }

        [Test]
        public void HandlesManyCustomDataSubscriptions()
        {
            var feed = RunDataFeed();
            for (int i = 0; i < 100; i++)
            {
                _algorithm.AddData<CustomMockedFileBaseData>((100 + i).ToString(), Resolution.Second, fillDataForward: false);
            }

            int count = 0;
            var emittedData = false;
            var stopwatch = Stopwatch.StartNew();

            var previousTime = DateTime.Now;
            Console.WriteLine("start: " + previousTime.ToString("o"));
            ConsumeBridge(feed, TimeSpan.FromSeconds(3), false, ts =>
            {
                // because this is a remote file we may skip data points while the newest
                // version of the file is downloading [internet speed] and also we decide
                // not to emit old data
                stopwatch.Stop();
                if (ts.Slice.Count == 0) return;

                emittedData = true;
                count++;

                // make sure within 2 seconds
                var delta = DateTime.Now.Subtract(previousTime);
                previousTime = DateTime.Now;
                Assert.IsTrue(delta <= TimeSpan.FromSeconds(2), delta.ToString());
                ConsoleWriteLine("TimeProvider now: " + _manualTimeProvider.GetUtcNow() + " Count: "
                                  + ts.Slice.Count + ". Delta (ms): "
                                  + ((decimal)delta.TotalMilliseconds).SmartRounding() + Environment.NewLine);
            });

            Console.WriteLine("Count: " + count);
            Console.WriteLine("Spool up time: " + stopwatch.Elapsed);

            Assert.That(count, Is.GreaterThan(5));
            Assert.IsTrue(emittedData);
        }

        [TestCase(FileFormat.Csv)]
        [TestCase(FileFormat.Collection)]
        public void RestCustomDataReturningNullDoesNotInfinitelyPoll(FileFormat fileFormat)
        {
            TestCustomData.FileFormat = fileFormat;

            var feed = RunDataFeed();

            _algorithm.AddData<TestCustomData>("Pinocho", Resolution.Minute, fillDataForward: false);

            TestCustomData.ReturnNull = true;
            ConsumeBridge(feed, TimeSpan.FromSeconds(2), false, ts =>
            {
                Console.WriteLine("Emitted data");
            });

            Assert.AreEqual(TestCustomData.ReaderCallsCount, 1);
        }

        [TestCase(FileFormat.Csv)]
        [TestCase(FileFormat.Collection)]
        public void RestCustomDataReturningOldDataDoesNotInfinitelyPoll(FileFormat fileFormat)
        {
            TestCustomData.FileFormat = fileFormat;

            var feed = RunDataFeed();

            _algorithm.AddData<TestCustomData>("Pinocho", Resolution.Hour, fillDataForward: false);

            TestCustomData.ReturnNull = false;
            ConsumeBridge(feed, TimeSpan.FromSeconds(2), false, ts =>
            {
                Console.WriteLine("Emitted data");
            });

            Assert.AreEqual(TestCustomData.ReaderCallsCount, 1);
        }

        [TestCase(FileFormat.Csv)]
        [TestCase(FileFormat.Collection)]
        public void RestCustomDataThrowingExceptionDoesNotInfinitelyPoll(FileFormat fileFormat)
        {
            TestCustomData.FileFormat = fileFormat;

            var feed = RunDataFeed();

            _algorithm.AddData<TestCustomData>("Pinocho", Resolution.Hour, fillDataForward: false);

            TestCustomData.ThrowException = true;
            ConsumeBridge(feed, TimeSpan.FromSeconds(2), false, ts =>
            {
                Console.WriteLine("Emitted data");
            });

            Assert.AreEqual(TestCustomData.ReaderCallsCount, 1);
        }

        [Test, Ignore("These tests depend on a remote server")]
        public void HandlesRestApi()
        {
            var resolution = Resolution.Second;
            FuncDataQueueHandler dqgh;
            var feed = RunDataFeed(out dqgh);
            _algorithm.AddData<RestApiBaseData>("RestApi", resolution);
            var symbol = SymbolCache.GetSymbol("RestApi");

            var count = 0;
            var receivedData = false;
            var timeZone = _algorithm.Securities[symbol].Exchange.TimeZone;
            RestApiBaseData last = null;

            var cancellationTokenSource = new CancellationTokenSource();
            foreach (var ts in _synchronizer.StreamData(cancellationTokenSource.Token))
            {
                if (!ts.Slice.ContainsKey(symbol)) return;

                count++;
                receivedData = true;
                var data = (RestApiBaseData)ts.Slice[symbol];
                var time = data.EndTime.ConvertToUtc(timeZone);
                ConsoleWriteLine(DateTime.UtcNow + ": Data time: " + time.ConvertFromUtc(TimeZones.NewYork) + Environment.NewLine);
                if (last != null)
                {
                    Assert.AreEqual(last.EndTime, data.EndTime.Subtract(resolution.ToTimeSpan()));
                }
                last = data;
            }

            feed.Exit();
            Assert.That(count, Is.GreaterThanOrEqualTo(8));
            Assert.IsTrue(receivedData);
            Assert.That(RestApiBaseData.ReaderCount, Is.LessThanOrEqualTo(30)); // we poll at 10x frequency

            Console.WriteLine("Count: " + count + " ReaderCount: " + RestApiBaseData.ReaderCount);
        }

        [Test]
        public void CoarseFundamentalDataGetsPipedCorrectly()
        {
            var lck = new object();
            BaseDataCollection list = null;
            var timer = new Timer(state =>
            {
                var currentTime = _manualTimeProvider.GetUtcNow().ConvertFromUtc(TimeZones.NewYork);
                lock (state)
                {
                    list = new BaseDataCollection { Symbol = CoarseFundamental.CreateUniverseSymbol(Market.USA, false) };
                    list.Data.Add(new CoarseFundamental
                    {
                        Symbol = Symbols.SPY,
                        Time = currentTime - Time.OneDay, // hard-coded coarse period of one day
                    });
                }
            }, lck, TimeSpan.FromSeconds(0.5), TimeSpan.FromMilliseconds(-1));

            var yieldedUniverseData = false;
            var feed = RunDataFeed(getNextTicksFunction: fdqh =>
            {
                lock (lck)
                {
                    if (list != null)
                        try
                        {
                            var tmp = list;
                            return new List<BaseData> { tmp };
                        }
                        finally
                        {
                            list = null;
                            yieldedUniverseData = true;
                        }
                }
                return Enumerable.Empty<BaseData>();
            });

            _algorithm.AddUniverse(coarse => coarse.Take(10).Select(x => x.Symbol));

            var receivedCoarseData = false;
            ConsumeBridge(feed, TimeSpan.FromSeconds(2), ts =>
            {
                if (ts.UniverseData.Count > 0 &&
                    ts.UniverseData.First().Value.Data.First() is CoarseFundamental)
                {
                    receivedCoarseData = true;
                }
            }, sendUniverseData: true);

            timer.Dispose();
            Assert.IsTrue(yieldedUniverseData);
            Assert.IsTrue(receivedCoarseData);
        }
        [Test]
        public void CoarseFundamentalDataIsHoldUntilTimeIsRight()
        {
            var startDate = new DateTime(2018, 08, 1, 23, 0, 0);
            _manualTimeProvider.SetCurrentTimeUtc(startDate);
            Console.WriteLine($"StartTime {_manualTimeProvider.GetUtcNow()}");

            // we just want to emit one single coarse data packet
            var yieldUniverseData = false;
            var yieldedUniverseData = false;
            var feed = RunDataFeed(getNextTicksFunction: fdqh =>
            {
                // just once
                if (yieldUniverseData)
                {
                    yieldedUniverseData = true;
                    var currentTime = _manualTimeProvider.GetUtcNow();
                    var data = new BaseDataCollection(currentTime, CoarseFundamental.CreateUniverseSymbol(Market.USA, false),
                        new []{new CoarseFundamental
                        {
                            Symbol = Symbols.SPY,
                            Time = currentTime - Time.OneDay
                        }});
                    Console.WriteLine($"Emitted BaseDataCollection {data.Time} {data.EndTime}");
                    yieldUniverseData = false;

                    // Assert data gets emitted in an 'invalid' time
                    Assert.IsTrue(data.Time.Hour > 23 || data.Time.Hour < 5);
                    return new[] { data };
                }
                return Enumerable.Empty<BaseData>();
            });

            _algorithm.AddUniverse(coarse => coarse.Take(10).Select(x => x.Symbol));

            var receivedCoarseData = false;
            ConsumeBridge(feed, TimeSpan.FromSeconds(5), ts =>
            {
                if (!yieldedUniverseData)
                {
                    yieldUniverseData = true;
                }
                if (ts.UniverseData.Count > 0 &&
                    ts.UniverseData.First().Value.Data.First() is CoarseFundamental)
                {
                    var now = _manualTimeProvider.GetUtcNow();
                    Console.WriteLine($"Received BaseDataCollection {now}");

                    // Assert data got hold until time was right
                    Assert.IsTrue(now.Hour < 23 && now.Hour > 5);
                    receivedCoarseData = true;
                }
            }, sendUniverseData: true,
                alwaysInvoke:true,
                secondsTimeStep: 3600,
                endDate: startDate.AddDays(1));

            Console.WriteLine($"EndTime {_manualTimeProvider.GetUtcNow()}");

            Assert.IsTrue(yieldedUniverseData);
            Assert.IsTrue(receivedCoarseData);
        }

        [Test]
        public void FineCoarseFundamentalDataGetsPipedCorrectly()
        {
            var lck = new object();
            BaseDataCollection list = null;
            var timer = new Timer(state =>
            {
                lock (state)
                {
                    list = new BaseDataCollection { Symbol = CoarseFundamental.CreateUniverseSymbol(Market.USA, false) };
                    list.Data.Add(new CoarseFundamental
                    {
                        Symbol = Symbols.AAPL,
                        Time = new DateTime(2014, 04, 24)
                    });
                }
            }, lck, TimeSpan.FromSeconds(0.5), TimeSpan.FromMilliseconds(-1));

            var yieldedUniverseData = false;
            var feed = RunDataFeed(getNextTicksFunction: fdqh =>
            {
                lock (lck)
                {
                    if (list != null)
                        try
                        {
                            var tmp = list;
                            return new List<BaseData> { tmp };
                        }
                        finally
                        {
                            list = null;
                            yieldedUniverseData = true;
                        }
                }
                return Enumerable.Empty<BaseData>();
            });

            var fineWasCalled = false;
            _algorithm.AddUniverse(coarse => coarse.Take(1).Select(x => x.Symbol),
                fine =>
                {
                    var symbol = fine.First().Symbol;
                    if (symbol == Symbols.AAPL)
                    {
                        fineWasCalled = true;
                    }
                    return new[] {symbol};
                });

            var receivedFundamentalsData = false;
            ConsumeBridge(feed, TimeSpan.FromSeconds(2), ts =>
            {
                if (ts.UniverseData.Count > 0 &&
                    ts.UniverseData.First().Value.Data.First() is Fundamentals)
                {
                    receivedFundamentalsData = true;
                }
            }, sendUniverseData: true);

            timer.Dispose();
            Assert.IsTrue(yieldedUniverseData);
            Assert.IsTrue(receivedFundamentalsData);
            Assert.IsTrue(fineWasCalled);
        }

        [Test]
        public void HandlesCoarseFundamentalData()
        {
            Symbol symbol = CoarseFundamental.CreateUniverseSymbol(Market.USA);
            _algorithm.AddUniverse(new FuncUniverse(
                new SubscriptionDataConfig(typeof(CoarseFundamental), symbol, Resolution.Daily, TimeZones.NewYork, TimeZones.NewYork, false, false, false),
                new UniverseSettings(Resolution.Second, 1, true, false, TimeSpan.Zero), SecurityInitializer.Null,
                coarse => coarse.Take(10).Select(x => x.Symbol)
                ));

            var lck = new object();
            BaseDataCollection list = null;
            const int coarseDataPointCount = 100000;
            var timer = new Timer(state =>
            {
                var currentTime = DateTime.UtcNow.ConvertFromUtc(TimeZones.NewYork);
                Console.WriteLine(currentTime + ": timer.Elapsed");

                lock (state)
                {
                    list = new BaseDataCollection { Symbol = symbol };
                    list.Data.AddRange(Enumerable.Range(0, coarseDataPointCount).Select(x => new CoarseFundamental
                    {
                        Symbol = SymbolCache.GetSymbol(x.ToString()),
                        Time = currentTime - Time.OneDay, // hard-coded coarse period of one day
                    }));
                }
            }, lck, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(500));

            bool yieldedUniverseData = false;
            var feed = RunDataFeed(getNextTicksFunction : fdqh =>
            {
                lock (lck)
                {
                    if (list != null)
                        try
                        {
                            var tmp = list;
                            return new List<BaseData> { tmp };
                        }
                        finally
                        {
                            list = null;
                            yieldedUniverseData = true;
                        }
                }
                return Enumerable.Empty<BaseData>();
            });


            ConsumeBridge(feed, TimeSpan.FromSeconds(5), ts =>
            {
                Assert.IsTrue(_dataManager.DataFeedSubscriptions
                    .Any(x => x.IsUniverseSelectionSubscription));
            });

            timer.Dispose();
            Assert.IsTrue(yieldedUniverseData);
        }


        [Test]
        public void FastExitsDoNotThrowUnhandledExceptions()
        {
            var algorithm = new AlgorithmStub();

            // job is used to send into DataQueueHandler
            var job = new LiveNodePacket();

            // result handler is used due to dependency in SubscriptionDataReader
            var resultHandler = new BacktestingResultHandler();

            var feed = new TestableLiveTradingDataFeed();
            var marketHoursDatabase = MarketHoursDatabase.FromDataFolder();
            var symbolPropertiesDataBase = SymbolPropertiesDatabase.FromDataFolder();
            var securityService = new SecurityService(
                algorithm.Portfolio.CashBook,
                marketHoursDatabase,
                symbolPropertiesDataBase,
                algorithm);
            algorithm.Securities.SetSecurityService(securityService);
            var dataManager = new DataManager(feed,
                new UniverseSelection(algorithm, securityService),
                algorithm,
                algorithm.TimeKeeper,
                marketHoursDatabase,
                true);
            algorithm.SubscriptionManager.SetDataManager(dataManager);
            var synchronizer = new TestableLiveSynchronizer();
            synchronizer.Initialize(algorithm, dataManager);
            algorithm.AddSecurities(Resolution.Tick, Enumerable.Range(0, 20).Select(x => x.ToString()).ToList());
            var getNextTicksFunction = Enumerable.Range(0, 20).Select(x => new Tick { Symbol = SymbolCache.GetSymbol(x.ToString()) }).ToList();
            feed.DataQueueHandler = new FuncDataQueueHandler(handler => getNextTicksFunction);
            var mapFileProvider = new LocalDiskMapFileProvider();
            var fileProvider = new DefaultDataProvider();
            feed.Initialize(
                algorithm,
                job,
                resultHandler,
                mapFileProvider,
                new LocalDiskFactorFileProvider(mapFileProvider),
                fileProvider,
                dataManager,
                synchronizer);

            var unhandledExceptionWasThrown = false;
            try
            {
                feed.Exit();
            }
            catch (Exception ex)
            {
                QuantConnect.Logging.Log.Error(ex.ToString());
                unhandledExceptionWasThrown = true;
            }

            Thread.Sleep(500);
            Assert.IsFalse(unhandledExceptionWasThrown);
        }

        [Test]
        public void HandlesAllTickTypesAtTickResolution()
        {
            var symbol = Symbol.Create("BTCUSD", SecurityType.Crypto, Market.GDAX);

            var feed = RunDataFeed(
                Resolution.Tick,
                crypto: new List<string> { symbol.Value },
                getNextTicksFunction: dqh => Enumerable.Range(0, 2)
                    .Select(x => new Tick
                    {
                        Symbol = symbol,
                        TickType = x % 2 == 0 ? TickType.Trade : TickType.Quote
                    })
                    .ToList());

            var emittedData = false;
            ConsumeBridge(feed, TimeSpan.FromSeconds(1), true, ts =>
            {
                if (ts.Slice.HasData)
                {
                    emittedData = true;
                    foreach (var security in _algorithm.Securities.Values)
                    {
                        foreach (var config in security.Subscriptions)
                        {
                            Assert.IsTrue(ts.ConsolidatorUpdateData.Count == 2); // Trades + Quotes
                            Assert.IsTrue(ts.ConsolidatorUpdateData.Select(x => x.Target).Contains(config));
                            Assert.IsTrue(ts.ConsolidatorUpdateData.All(x => x.Data.Count > 0));
                        }
                    }
                }
            });

            Assert.IsTrue(emittedData);
        }

        [Test]
        public void SuspiciousTicksAreNotFilteredAtTickResolution()
        {
            var symbol = Symbol.Create("SPY", SecurityType.Equity, Market.USA);

            var feed = RunDataFeed(
                Resolution.Tick,
                equities: new List<string> { symbol.Value },
                getNextTicksFunction: dqh => Enumerable.Range(0, 1)
                    .Select(
                        x => new Tick
                        {
                            Symbol = symbol,
                            TickType = TickType.Trade,
                            Suspicious = x % 2 == 0
                        })
                    .ToList());

            var emittedData = false;
            var suspiciousTicksReceived = false;
            ConsumeBridge(feed, TimeSpan.FromSeconds(3), true, ts =>
            {
                if (ts.Slice.HasData)
                {
                    emittedData = true;

                    foreach (var kvp in ts.Slice.Ticks)
                    {
                        foreach (var tick in kvp.Value)
                        {
                            if (tick.Suspicious) suspiciousTicksReceived = true;
                        }
                    }
                }
            });

            Assert.IsTrue(emittedData);
            Assert.IsTrue(suspiciousTicksReceived);
        }

        [TestCase(SecurityType.Equity, TickType.Trade)]
        [TestCase(SecurityType.Forex, TickType.Quote)]
        [TestCase(SecurityType.Crypto, TickType.Trade)]
        [TestCase(SecurityType.Crypto, TickType.Quote)]
        public void SuspiciousTicksAreFilteredAtNonTickResolution(SecurityType securityType, TickType tickType)
        {
            var lastTime = _manualTimeProvider.GetUtcNow();
            var feed = RunDataFeed(
                resolution: Resolution.Minute,
                equities: securityType == SecurityType.Equity ? new List<string> { Symbols.SPY } : new List<string>(),
                forex: securityType == SecurityType.Forex ? new List<string> { Symbols.EURUSD } : new List<string>(),
                crypto: securityType == SecurityType.Crypto ? new List<string> { Symbols.BTCUSD } : new List<string>(),
                getNextTicksFunction: (fdqh =>
                {
                    var time = _manualTimeProvider.GetUtcNow();
                    if (time == lastTime) return Enumerable.Empty<BaseData>();
                    lastTime = time;
                    var tickTime = lastTime.AddMinutes(-1).ConvertFromUtc(TimeZones.NewYork);
                    return fdqh.Subscriptions.Where(symbol => !_algorithm.UniverseManager.ContainsKey(symbol)) // its not a universe
                        .Select(symbol => new Tick(tickTime, symbol, 1, 2)
                        {
                            Quantity = 1,
                            TickType = tickType,
                            Suspicious = true
                        }).ToList();
                }));

            var emittedData = false;
            ConsumeBridge(feed, TimeSpan.FromSeconds(2), true, ts =>
            {
                if (ts.Slice.HasData)
                {
                    emittedData = true;
                }
            });

            Assert.IsFalse(emittedData);
        }

        [Test]
        public void HandlesAuxiliaryDataAtTickResolution()
        {
            var symbol = Symbols.AAPL;

            var feed = RunDataFeed(
                Resolution.Tick,
                equities: new List<string> { symbol.Value },
                getNextTicksFunction: delegate
                {
                    return Enumerable.Range(0, 2)
                        .Select(
                            x => x % 2 == 0
                                ? (BaseData) new Tick { Symbol = symbol, TickType = TickType.Trade }
                                : new Dividend { Symbol = symbol, Value = x })
                        .ToList();
                });

            var emittedTicks = false;
            var emittedAuxData = false;
            ConsumeBridge(feed, TimeSpan.FromSeconds(1), true, ts =>
            {
                if (ts.Slice.HasData)
                {
                    if (ts.Slice.Ticks.ContainsKey(symbol))
                    {
                        emittedTicks = true;
                    }
                    if (ts.Slice.Dividends.ContainsKey(symbol))
                    {
                        emittedAuxData = true;
                    }
                }
            });

            Assert.IsTrue(emittedTicks);
            Assert.IsTrue(emittedAuxData);
        }

        private IDataFeed RunDataFeed(Resolution resolution = Resolution.Second,
                                    List<string> equities = null,
                                    List<string> forex = null,
                                    List<string> crypto = null,
                                    Func<FuncDataQueueHandler, IEnumerable<BaseData>> getNextTicksFunction = null)
        {
            FuncDataQueueHandler dataQueueHandler;
            return RunDataFeed(out dataQueueHandler, getNextTicksFunction, resolution, equities, forex, crypto);
        }

        private IDataFeed RunDataFeed(out FuncDataQueueHandler dataQueueHandler, Func<FuncDataQueueHandler, IEnumerable<BaseData>> getNextTicksFunction = null,
            Resolution resolution = Resolution.Second, List<string> equities = null, List<string> forex = null, List<string> crypto = null)
        {
            _algorithm.SetStartDate(_startDate);

            var lastTime = _manualTimeProvider.GetUtcNow();
            getNextTicksFunction = getNextTicksFunction ?? (fdqh =>
            {
                var time = _manualTimeProvider.GetUtcNow();
                if (time == lastTime) return Enumerable.Empty<BaseData>();
                lastTime = time;
                var tickTime = lastTime.AddMinutes(-1).ConvertFromUtc(TimeZones.NewYork);
                return fdqh.Subscriptions.Where(symbol => !_algorithm.UniverseManager.ContainsKey(symbol)) // its not a universe
                    .Select(symbol => new Tick(tickTime, symbol, 1, 2)
                    {
                        Quantity = 1,
                        // Symbol could not be in the Securities collections for the custom Universe tests. AlgorithmManager is in charge of adding them, and we are not executing that code here.
                        TickType = _algorithm.Securities.ContainsKey(symbol) ? _algorithm.Securities[symbol].SubscriptionDataConfig.TickType : TickType.Trade
                    }).ToList();
            });

            // job is used to send into DataQueueHandler
            var job = new LiveNodePacket();
            // result handler is used due to dependency in SubscriptionDataReader
            var resultHandler = new BacktestingResultHandler();

            dataQueueHandler = new FuncDataQueueHandler(getNextTicksFunction);

            var feed = new TestableLiveTradingDataFeed(dataQueueHandler);
            var mapFileProvider = new LocalDiskMapFileProvider();
            var fileProvider = new DefaultDataProvider();
            var marketHoursDatabase = MarketHoursDatabase.FromDataFolder();
            var symbolPropertiesDataBase = SymbolPropertiesDatabase.FromDataFolder();
            var securityService = new SecurityService(_algorithm.Portfolio.CashBook, marketHoursDatabase, symbolPropertiesDataBase, _algorithm);
            _algorithm.Securities.SetSecurityService(securityService);
            _dataManager = new DataManager(feed,
                new UniverseSelection(_algorithm, securityService),
                _algorithm,
                _algorithm.TimeKeeper,
                marketHoursDatabase,
                true);
            _algorithm.SubscriptionManager.SetDataManager(_dataManager);
            _algorithm.AddSecurities(resolution, equities, forex, crypto);
            _synchronizer = new TestableLiveSynchronizer(_manualTimeProvider);
            _synchronizer.Initialize(_algorithm, _dataManager);

            feed.Initialize(_algorithm, job, resultHandler, mapFileProvider,
                new LocalDiskFactorFileProvider(mapFileProvider), fileProvider, _dataManager, _synchronizer);

            _algorithm.PostInitialize();
            Thread.Sleep(150); // small handicap for the data to be pumped so TimeSlices have data of all subscriptions
            return feed;
        }

        private void ConsumeBridge(IDataFeed feed, TimeSpan timeout, Action<TimeSlice> handler, bool sendUniverseData = false,
            int secondsTimeStep = 1, bool alwaysInvoke = false, DateTime endDate = default(DateTime))
        {
            ConsumeBridge(feed, timeout, alwaysInvoke, handler, sendUniverseData: sendUniverseData, secondsTimeStep: secondsTimeStep, endDate: endDate);
        }

        private void ConsumeBridge(IDataFeed feed,
            TimeSpan timeout,
            bool alwaysInvoke,
            Action<TimeSlice> handler,
            bool noOutput = true,
            bool sendUniverseData = false,
            int secondsTimeStep = 1,
            DateTime endDate = default(DateTime))
        {
            var endTime = DateTime.UtcNow.Add(timeout);
            bool startedReceivingata = false;
            var cancellationTokenSource = new CancellationTokenSource();
            foreach (var timeSlice in _synchronizer.StreamData(cancellationTokenSource.Token))
            {
                if (!noOutput)
                {
                    ConsoleWriteLine("\r\n" + $"Now (EDT): {DateTime.UtcNow.ConvertFromUtc(TimeZones.NewYork):o}" +
                                     $". TimeSlice.Time (EDT): {timeSlice.Time.ConvertFromUtc(TimeZones.NewYork):o}");
                }

                if (!startedReceivingata
                    && (timeSlice.Slice.Count != 0
                        || sendUniverseData && timeSlice.UniverseData.Count > 0))
                {
                    startedReceivingata = true;
                }
                if (startedReceivingata || alwaysInvoke)
                {
                    handler(timeSlice);
                }
                _algorithm.OnEndOfTimeStep();
                _manualTimeProvider.AdvanceSeconds(secondsTimeStep);
                Thread.Sleep(1);
                if (endDate != default(DateTime) && _manualTimeProvider.GetUtcNow() > endDate
                    || endTime <= DateTime.UtcNow)
                {
                    feed.Exit();
                    cancellationTokenSource.Cancel();
                    return;
                }
            }
        }

        private class Count
        {
            public int Value;
        }

        private static IEnumerable<BaseData> ProduceBenchmarkTicks(FuncDataQueueHandler fdqh, Count count)
        {
            for (int i = 0; i < 10000; i++)
            {
                foreach (var symbol in fdqh.Subscriptions)
                {
                    count.Value++;
                    yield return new Tick { Symbol = symbol };
                }
            }
        }

        private void ConsoleWriteLine(string line = "")
        {
            if (LogsEnabled)
            {
                Console.WriteLine(line);
            }
        }

        public TestCaseData[] DataTypeTestCases => new[]
        {
            // Equity - Hourly resolution
            // We expect 7 hourly bars for 6.5 hours in open market hours
            // We expect only 1 dividend at midnight
            new TestCaseData(Symbols.SPY, Resolution.Hour, 1, 0, 7, 0, 1),

            // Equity - Minute resolution
            // We expect 390 minute bars for 6.5 hours in open market hours
            // We expect only 1 dividend at midnight
            new TestCaseData(Symbols.SPY, Resolution.Minute, 1, 0, (int)(6.5 * 60), 0, 1),

            // Equity - Tick resolution
            // In this test we only emit ticks once per hour
            // We expect only 6 ticks -- the 4 PM tick is not received because it's outside market hours
            // We expect only 1 dividend at midnight
            new TestCaseData(Symbols.SPY, Resolution.Tick, 1, 7 - 1, 0, 0, 1),

            // Forex - FXCM
            new TestCaseData(Symbols.EURUSD, Resolution.Hour, 1, 0, 0, 24, 0),
            new TestCaseData(Symbols.EURUSD, Resolution.Minute, 1, 0, 0, 24 * 60, 0),
            new TestCaseData(Symbols.EURUSD, Resolution.Tick, 1, 24, 0, 0, 0),

            // Forex - Oanda
            new TestCaseData(Symbol.Create("EURUSD", SecurityType.Forex, Market.Oanda), Resolution.Hour, 1, 0, 0, 24, 0),
            new TestCaseData(Symbol.Create("EURUSD", SecurityType.Forex, Market.Oanda), Resolution.Minute, 1, 0, 0, 24 * 60, 0),
            new TestCaseData(Symbol.Create("EURUSD", SecurityType.Forex, Market.Oanda), Resolution.Tick, 1, 24, 0, 0, 0),

            // CFD - FXCM
            new TestCaseData(Symbols.DE30EUR, Resolution.Hour, 1, 0, 0, 14, 0),
            new TestCaseData(Symbols.DE30EUR, Resolution.Minute, 1, 0, 0, 14 * 60, 0),
            new TestCaseData(Symbols.DE30EUR, Resolution.Tick, 1, 14, 0, 0, 0),

            // CFD - Oanda
            new TestCaseData(Symbol.Create("DE30EUR", SecurityType.Cfd, Market.Oanda), Resolution.Hour, 1, 0, 0, 14, 0),
            new TestCaseData(Symbol.Create("DE30EUR", SecurityType.Cfd, Market.Oanda), Resolution.Minute, 1, 0, 0, 14 * 60, 0),
            new TestCaseData(Symbol.Create("DE30EUR", SecurityType.Cfd, Market.Oanda), Resolution.Tick, 1, 14, 0, 0, 0),

            // Crypto
            new TestCaseData(Symbols.BTCUSD, Resolution.Hour, 1, 0, 24, 24, 0),
            new TestCaseData(Symbols.BTCUSD, Resolution.Minute, 1, 0, 24 * 60, 24 * 60, 0),
            new TestCaseData(Symbols.BTCUSD, Resolution.Tick, 1, 24, 0, 0, 0),

            // Futures
            // ES has two session breaks totalling 1h 15m, so total trading hours = 22.75
            new TestCaseData(Symbols.Future_ESZ18_Dec2018, Resolution.Hour, 1, 0, 23, 23, 0),
            new TestCaseData(Symbols.Future_ESZ18_Dec2018, Resolution.Minute, 1, 0, (int)(22.75 * 60), (int)(22.75 * 60), 0),
            new TestCaseData(Symbols.Future_ESZ18_Dec2018, Resolution.Tick, 1, 23, 0, 0, 0),

            // Options
            new TestCaseData(Symbols.SPY_C_192_Feb19_2016, Resolution.Hour, 1, 0, 7, 7, 0),
            new TestCaseData(Symbols.SPY_C_192_Feb19_2016, Resolution.Minute, 1, 0, (int)(6.5 * 60), (int)(6.5 * 60), 0),
            new TestCaseData(Symbols.SPY_C_192_Feb19_2016, Resolution.Tick, 1, 7 - 1, 0, 0, 0),

            // Custom (streaming not supported yet)
            new TestCaseData(Symbol.Create("CUSTOM", SecurityType.Base, Market.USA), Resolution.Daily, 4, 0, 0, 0, 0)
        };

        [TestCaseSource(nameof(DataTypeTestCases))]
        public void HandlesAllTypes(
            Symbol symbol,
            Resolution resolution,
            int days,
            int expectedTicksReceived,
            int expectedTradeBarsReceived,
            int expectedQuoteBarsReceived,
            int expectedAuxPointsReceived)
        {
            // startDate and endDate are in algorithm time zone
            var startDate = new DateTime(2019, 6, 3);
            var endDate = startDate.AddDays(days);

            var algorithmTimeZone = TimeZones.NewYork;

            var exchangeTimeZone = TimeZones.NewYork;
            if (symbol.SecurityType == SecurityType.Crypto)
            {
                exchangeTimeZone = TimeZones.Utc;
            }
            else if (symbol.ID.Market == Market.FXCM)
            {
                exchangeTimeZone = TimeZones.EasternStandard;
            }

            var timeProvider = new ManualTimeProvider(algorithmTimeZone);
            timeProvider.SetCurrentTime(startDate);

            var actualPricePointsEnqueued = 0;
            var actualAuxPointsEnqueued = 0;
            var lastTime = DateTime.MinValue;
            var startedReceivingData = false;

            var dataQueueHandler = new FuncDataQueueHandler(fdqh =>
            {
                var utcTime = timeProvider.GetUtcNow();
                var time = utcTime.ConvertFromUtc(exchangeTimeZone);
                if (time == lastTime || time > endDate.ConvertTo(algorithmTimeZone, exchangeTimeZone) || !startedReceivingData)
                {
                    return Enumerable.Empty<BaseData>();
                }
                lastTime = time;

                var dataPoints = new List<BaseData>();

                if (symbol.SecurityType == SecurityType.Base)
                {
                    var dataPoint = new Quandl
                    {
                        Symbol = symbol,
                        EndTime = time,
                        Value = actualPricePointsEnqueued
                    };

                    dataPoints.Add(dataPoint);
                    actualPricePointsEnqueued++;

                    if (LogsEnabled)
                    {
                        Log.Trace($"{utcTime} - FuncDataQueueHandler emitted custom data point: {dataPoint}");
                    }
                }
                else
                {
                    if (symbol.SecurityType == SecurityType.Equity && time.Day == startDate.Day + 1 && time.Hour == 0 && time.Minute == 0)
                    {
                        var dividend = new Dividend
                        {
                            Symbol = symbol,
                            Time = time,
                            EndTime = time,
                            Value = actualAuxPointsEnqueued
                        };

                        dataPoints.Add(dividend);
                        actualAuxPointsEnqueued++;

                        if (LogsEnabled)
                        {
                            Log.Trace($"{utcTime} - FuncDataQueueHandler emitted dividend: {dividend}");
                        }
                    }
                    else
                    {
                        var tickType = symbol.SecurityType == SecurityType.Equity ? TickType.Trade : TickType.Quote;

                        var dataPoint = new Tick
                        {
                            Symbol = symbol,
                            Time = time,
                            EndTime = time,
                            TickType = tickType,
                            Value = actualPricePointsEnqueued
                        };
                        dataPoints.Add(dataPoint);

                        if (symbol.SecurityType == SecurityType.Crypto ||
                            symbol.SecurityType == SecurityType.Option ||
                            symbol.SecurityType == SecurityType.Future)
                        {
                            dataPoint = new Tick
                            {
                                Symbol = symbol,
                                Time = time,
                                EndTime = time,
                                TickType = TickType.Trade,
                                Value = actualPricePointsEnqueued
                            };

                            dataPoints.Add(dataPoint);
                        }

                        actualPricePointsEnqueued++;

                        if (LogsEnabled)
                        {
                            Log.Trace($"{utcTime} - FuncDataQueueHandler emitted price tick: {dataPoint}");
                        }
                    }
                }


                return dataPoints;
            });

            var feed = new TestableLiveTradingDataFeed(dataQueueHandler);

            var algorithm = new QCAlgorithm();
            algorithm.SetDateTime(timeProvider.GetUtcNow());

            var historyProvider = new Mock<IHistoryProvider>();
            historyProvider.Setup(
                    m => m.GetHistory(It.IsAny<IEnumerable<Data.HistoryRequest>>(), It.IsAny<DateTimeZone>()))
                .Returns(Enumerable.Empty<Slice>());
            algorithm.SetHistoryProvider(historyProvider.Object);

            var marketHoursDatabase = MarketHoursDatabase.FromDataFolder();
            var symbolPropertiesDataBase = SymbolPropertiesDatabase.FromDataFolder();
            var securityService = new SecurityService(
                algorithm.Portfolio.CashBook,
                marketHoursDatabase,
                symbolPropertiesDataBase,
                algorithm);
            algorithm.Securities.SetSecurityService(securityService);
            var dataManager = new DataManager(feed,
                new UniverseSelection(algorithm, securityService),
                algorithm,
                algorithm.TimeKeeper,
                marketHoursDatabase,
                true);
            algorithm.SubscriptionManager.SetDataManager(dataManager);
            algorithm.SetLiveMode(true);

            var mock = new Mock<ITransactionHandler>();
            mock.Setup(m => m.GetOpenOrders(It.IsAny<Func<Order, bool>>())).Returns(new List<Order>());
            algorithm.Transactions.SetOrderProcessor(mock.Object);

            var synchronizer = new TestableLiveSynchronizer(timeProvider, TimeSpan.FromMilliseconds(50));
            synchronizer.Initialize(algorithm, dataManager);

            var mapFileProvider = new LocalDiskMapFileProvider();
            feed.Initialize(algorithm, new LiveNodePacket(), new BacktestingResultHandler(),
                mapFileProvider, new LocalDiskFactorFileProvider(mapFileProvider), new DefaultDataProvider(), dataManager, synchronizer);

            switch (symbol.SecurityType)
            {
                case SecurityType.Base:
                    algorithm.AddData<Quandl>(symbol.Value, resolution, fillDataForward: false);
                    break;

                case SecurityType.Future:
                    algorithm.AddFutureContract(symbol, resolution, fillDataForward: false);
                    break;

                case SecurityType.Option:
                    algorithm.AddOptionContract(symbol, resolution, fillDataForward: false);
                    break;

                default:
                    algorithm.AddSecurity(symbol.SecurityType, symbol.Value, resolution, symbol.ID.Market, false, 1, false);
                    break;
            }

            algorithm.PostInitialize();

            var cancellationTokenSource = new CancellationTokenSource();

            var actualTicksReceived = 0;
            var actualTradeBarsReceived = 0;
            var actualQuoteBarsReceived = 0;
            var actualAuxPointsReceived = 0;
            var sliceCount = 0;
            foreach (var timeSlice in synchronizer.StreamData(cancellationTokenSource.Token))
            {
                sliceCount++;

                if (!startedReceivingData && (timeSlice.Slice.Count != 0 || timeSlice.UniverseData.Count > 0))
                {
                    startedReceivingData = true;
                }

                if (startedReceivingData)
                {
                    if (resolution == Resolution.Tick)
                    {
                        if (timeSlice.Slice.Ticks.ContainsKey(symbol))
                        {
                            // assuming only one tick per slice
                            if (LogsEnabled)
                            {
                                Log.Trace($"{algorithm.UtcTime} - Tick received, value: {timeSlice.Slice.Ticks[symbol][0].Value}");
                            }

                            actualTicksReceived++;
                        }

                        if (timeSlice.Slice.Dividends.ContainsKey(symbol))
                        {
                            if (LogsEnabled)
                            {
                                Log.Trace($"{algorithm.UtcTime} - Dividend received, value: {timeSlice.Slice.Dividends[symbol].Value}");
                            }

                            actualAuxPointsReceived++;
                        }
                    }
                    else
                    {
                        if (timeSlice.Slice.Bars.ContainsKey(symbol))
                        {
                            if (LogsEnabled)
                            {
                                Log.Trace($"{algorithm.UtcTime} - TradeBar received, value: {timeSlice.Slice.Bars[symbol].Value}");
                            }

                            actualTradeBarsReceived++;
                        }

                        if (timeSlice.Slice.Dividends.ContainsKey(symbol))
                        {
                            if (LogsEnabled)
                            {
                                Log.Trace($"{algorithm.UtcTime} - Dividend received, value: {timeSlice.Slice.Dividends[symbol].Value}");
                            }

                            actualAuxPointsReceived++;
                        }

                        if (timeSlice.Slice.QuoteBars.ContainsKey(symbol))
                        {
                            if (LogsEnabled)
                            {
                                Log.Trace($"{algorithm.UtcTime} - QuoteBar received, value: {timeSlice.Slice.QuoteBars[symbol].Value}");
                            }

                            actualQuoteBarsReceived++;
                        }
                    }
                }

                algorithm.OnEndOfTimeStep();

                // for tick resolution, we advance one hour at a time for less unit test run time
                timeProvider.Advance(resolution == Resolution.Tick ? TimeSpan.FromHours(1) : resolution.ToTimeSpan());
                var currentTime = timeProvider.GetUtcNow();
                algorithm.SetDateTime(currentTime);

                if (currentTime.ConvertFromUtc(algorithmTimeZone) > endDate)
                {
                    feed.Exit();
                    cancellationTokenSource.Cancel();
                    break;
                }

                Thread.Sleep(50);
            }

            Log.Trace($"SliceCount:{sliceCount} - PriceData: Enqueued:{actualPricePointsEnqueued} Received:{actualTradeBarsReceived}");
            Log.Trace($"SliceCount:{sliceCount} - AuxData: Enqueued:{actualAuxPointsEnqueued} Received:{actualAuxPointsReceived}");

            Assert.IsTrue(actualPricePointsEnqueued > 0);

            if (resolution == Resolution.Tick)
            {
                Assert.IsTrue(actualTicksReceived > 0);
            }
            else
            {
                switch (symbol.SecurityType)
                {
                    case SecurityType.Equity:
                        Assert.IsTrue(actualTradeBarsReceived > 0);
                        break;

                    case SecurityType.Forex:
                    case SecurityType.Cfd:
                        Assert.IsTrue(actualQuoteBarsReceived > 0);
                        break;

                    case SecurityType.Crypto:
                    case SecurityType.Option:
                    case SecurityType.Future:
                        Assert.IsTrue(actualTradeBarsReceived > 0);
                        Assert.IsTrue(actualQuoteBarsReceived > 0);
                        break;
                }
            }

            Assert.AreEqual(expectedTicksReceived, actualTicksReceived);
            Assert.AreEqual(expectedTradeBarsReceived, actualTradeBarsReceived);
            Assert.AreEqual(expectedQuoteBarsReceived, actualQuoteBarsReceived);
            Assert.AreEqual(expectedAuxPointsReceived, actualAuxPointsReceived);
        }
    }

    internal class TestableLiveTradingDataFeed : LiveTradingDataFeed
    {
        public IDataQueueHandler DataQueueHandler;

        public TestableLiveTradingDataFeed(IDataQueueHandler dataQueueHandler = null)
        {
            DataQueueHandler = dataQueueHandler;
        }

        protected override IDataQueueHandler GetDataQueueHandler()
        {
            return DataQueueHandler;
        }
    }

    internal class TestableLiveSynchronizer : LiveSynchronizer
    {
        private readonly ITimeProvider _timeProvider;

        protected override TimeSpan NewLiveDataTimeout { get; }

        public TestableLiveSynchronizer(ITimeProvider timeProvider = null, TimeSpan? newLiveDataTimeout = null)
        {
            _timeProvider = timeProvider ?? new RealTimeProvider();
            NewLiveDataTimeout = newLiveDataTimeout ?? base.NewLiveDataTimeout;
        }

        protected override ITimeProvider GetTimeProvider()
        {
            return _timeProvider;
        }
    }

    internal class TestCustomData : BaseData
    {
        public static int ReaderCallsCount { get; set; }

        public static bool ReturnNull { get; set; }

        public static bool ThrowException { get; set; }

        public static FileFormat FileFormat { get; set; }

        static TestCustomData()
        {
            ReaderCallsCount = 0;
            FileFormat = FileFormat.Csv;
        }

        public override BaseData Reader(SubscriptionDataConfig config, string line, DateTime date, bool isLiveMode)
        {
            ReaderCallsCount++;
            if (ThrowException)
            {
                throw new Exception("Custom data Reader threw exception");
            }
            else if(ReturnNull)
            {
                return null;
            }
            else
            {
                var data = new TestCustomData
                {
                    // return not null but 'old data' -> there is no data yet available for today
                    Time = date.AddHours(-100),
                    Value = 1,
                    Symbol = config.Symbol
                };

                if (FileFormat == FileFormat.Collection)
                {
                    return new BaseDataCollection
                    {
                        Time = date.AddHours(-100),
                        // return not null but 'old data' -> there is no data yet available for today
                        EndTime = date.AddHours(-99),
                        Value = 1,
                        Symbol = config.Symbol,
                        Data = new List<BaseData> { data }
                    };
                }
                return data;
            }
        }

        public override SubscriptionDataSource GetSource(SubscriptionDataConfig config, DateTime date, bool isLiveMode)
        {
            return new SubscriptionDataSource("localhost:1232/fake",
                SubscriptionTransportMedium.Rest,
                FileFormat);
        }
    }
}
