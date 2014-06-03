﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.md in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNet.SignalR.Client;

namespace Microsoft.AspNet.SignalR.Crank
{
    public class Client
    {
        private static CrankArguments Arguments;
        private static ConcurrentBag<Connection> Connections = new ConcurrentBag<Connection>();
        private static ConcurrentBag<IHubProxy> HubProxies = new ConcurrentBag<IHubProxy>();
        private static HubConnection ControllerConnection;
        private static volatile IHubProxy ControllerProxy;
        private static ControllerEvents TestPhase = ControllerEvents.None;

        public static void Main()
        {
            try
            {
                Arguments = CrankArguments.Parse();

                ThreadPool.SetMinThreads(Arguments.Connections, 2);
                TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

                if (Arguments.IsController)
                {
                    ControllerHub.Start(Arguments);
                }

                string guid = "";
                string id = "";
                bool running = true;
                Task updateTask = null;

                HubConnection testManagerConnection = null; ;
                IHubProxy testManagerProxy = null;
                if (Arguments.TestManagerUrl != null)
                {
                    if (Arguments.TestManagerGuid == null)
                    {
                        throw new InvalidOperationException("A TestManagerGuid must be provided with TestManagerUrl.");
                    }
                    guid = Arguments.TestManagerGuid;
                    id = Process.GetCurrentProcess().Id.ToString();

                    testManagerConnection = new HubConnection(Arguments.TestManagerUrl);
                    testManagerProxy = testManagerConnection.CreateHubProxy("TestManagerHub");

                    testManagerProxy.On<string>("stopProcess", (processId) =>
                    {
                        if (processId.CompareTo(Process.GetCurrentProcess().Id.ToString()) == 0)
                        {
                            //await ControllerProxy.Invoke("signalPhaseChange", ControllerEvents.Disconnect);
                            TestPhase = ControllerEvents.Disconnect;
                        }
                    });

                    while (testManagerConnection.State == ConnectionState.Disconnected)
                    {
                        try
                        {
                            testManagerConnection.Start().Wait();
                        }
                        catch (Exception) { }
                    }

                    while (testManagerConnection.State != ConnectionState.Connected) ;
                    testManagerProxy.Invoke("join", Arguments.TestManagerGuid).Wait();

                    updateTask = Task.Factory.StartNew(async () =>
                    {
                        while (running)
                        {
                            var states = Connections.Select(c => c.State);

                            await testManagerProxy.Invoke("addUpdateProcess",
                                guid,
                                id,
                                TestPhase.ToString(),
                                states.Where(s => s == ConnectionState.Connected).Count(),
                                states.Where(s => s == ConnectionState.Reconnecting).Count(),
                                states.Where(s => s == ConnectionState.Disconnected).Count());
                            await Task.Delay(1000);
                        }
                    });
                }

                Run().Wait();

                if (testManagerProxy != null)
                {
                    running = false;
                    updateTask.Wait();

                    var states = Connections.Select(c => c.State);

                    testManagerProxy.Invoke("addUpdateProcess",
                        guid,
                        id,
                        "Terminated",
                        states.Where(s => s == ConnectionState.Connected).Count(),
                        states.Where(s => s == ConnectionState.Reconnecting).Count(),
                        states.Where(s => s == ConnectionState.Disconnected).Count()
                        ).Wait();
                    if (testManagerConnection != null)
                    {
                        testManagerConnection.Stop();
                    }
                }
            }
            catch (AggregateException aggregateException)
            {
                var e = aggregateException.InnerException;
                Console.Error.WriteLine(e.ToString());
                Console.Error.WriteLine(e.Message);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e.ToString());
                Console.Error.WriteLine(e.Message);
            }
        }

        private static async Task Run()
        {
            var remoteController = !Arguments.IsController || (Arguments.NumClients > 1);

            if (remoteController)
            {
                await OpenControllerConnection();
                Console.WriteLine("Waiting on Controller...");
            }

            while (TestPhase != ControllerEvents.Connect)
            {
                if (TestPhase == ControllerEvents.Abort)
                {
                    Console.WriteLine("Test Aborted");
                    return;
                }

                await Task.Delay(CrankArguments.ConnectionPollIntervalMS);
            }

            await RunConnect();
            await RunSend();

            RunDisconnect();

            if (remoteController)
            {
                CloseControllerConnection();
            }
        }

        private static async Task OpenControllerConnection()
        {
            ControllerConnection = new HubConnection(Arguments.ControllerUrl);
            ControllerProxy = ControllerConnection.CreateHubProxy("ControllerHub");

            ControllerProxy.On<ControllerEvents, int>("broadcast", (controllerEvent, id) =>
            {
                if (controllerEvent == ControllerEvents.Sample)
                {
                    OnSample(id);
                }
                else
                {
                    OnPhaseChanged(controllerEvent);
                }
            });

            int attempts = 0;

            while (true)
            {
                try
                {
                    await ControllerConnection.Start();

                    break;
                }
                catch
                {
                    attempts++;

                    if (attempts > CrankArguments.ConnectionPollAttempts)
                    {
                        throw new InvalidOperationException("Failed to connect to the controller hub");
                    }
                }

                await Task.Delay(CrankArguments.ConnectionPollIntervalMS);
            }
        }

        internal static void CloseControllerConnection()
        {
            if (ControllerConnection != null)
            {
                ControllerConnection.Stop();
                ControllerConnection = null;
                ControllerProxy = null;
            }
        }

        internal static void OnPhaseChanged(ControllerEvents phase)
        {
            Debug.Assert(phase != ControllerEvents.None);
            Debug.Assert(phase != ControllerEvents.Sample);

            TestPhase = phase;

            if (!Arguments.IsController)
            {
                Console.WriteLine("Running: {0}", Enum.GetName(typeof(ControllerEvents), phase));
            }
        }

        internal static void OnSample(int id)
        {
            var states = Connections.Select(c => c.State);

            var statesArr = new int[3]
            {
                states.Where(s => s == ConnectionState.Connected).Count(),
                states.Where(s => s == ConnectionState.Reconnecting).Count(),
                states.Where(s => s == ConnectionState.Disconnected).Count()
            };

            if (ControllerProxy != null)
            {
                ControllerProxy.Invoke("Mark", id, statesArr);
            }
            else
            {
                ControllerHub.MarkInternal(id, statesArr);
            }

            if (!Arguments.IsController)
            {
                Console.WriteLine("{0} Connected, {1} Reconnected, {2} Disconnected", statesArr[0], statesArr[1], statesArr[2]);
            }
        }

        private static void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            Console.Error.WriteLine(e.Exception.GetBaseException());
            e.SetObserved();
        }

        private static async Task RunSend()
        {
            var payload = (Arguments.SendBytes == 0) ? String.Empty : new string('a', Arguments.SendBytes);

            while (TestPhase == ControllerEvents.Send)
            {
                if (!String.IsNullOrEmpty(payload))
                {
                    await Task.WhenAll(Connections.Select(c => c.Send(payload)));
                }

                await Task.Delay(Arguments.SendInterval);
            }
        }

        private static void RunDisconnect()
        {
            if (Connections.Count > 0)
            {
                if ((TestPhase == ControllerEvents.Disconnect) ||
                    (TestPhase == ControllerEvents.Abort))
                {
                    Parallel.ForEach(Connections, c => c.Dispose());
                }
            }
        }

        private static async Task RunConnect()
        {
            var batched = Arguments.BatchSize > 1;

            while (TestPhase == ControllerEvents.Connect)
            {
                if (batched)
                {
                    await ConnectBatch();
                }
                else
                {
                    await ConnectSingle();
                }

                await Task.Delay(Arguments.ConnectInterval);
            }
        }

        private static async Task ConnectBatch()
        {
            var tasks = new Task[Arguments.BatchSize];

            for (int i = 0; i < Arguments.BatchSize; i++)
            {
                tasks[i] = ConnectSingle();
            }

            await Task.WhenAll(tasks);
        }

        private static async Task ConnectSingle()
        {
            var connection = CreateConnection();

            try
            {
                if (Arguments.Transport == null)
                {
                    await connection.Start();
                }
                else
                {
                    await connection.Start(Arguments.GetTransport());
                }

                connection.Closed += () =>
                {
                    Connections.TryTake(out connection);
                };

                Connections.Add(connection);
            }
            catch (Exception e)
            {
                Console.WriteLine("Connection.Start Failed: {0}: {1}", e.GetType(), e.Message);
            }
        }

        private static Connection CreateConnection()
        {
            return new Connection(Arguments.Url);
        }
    }
}