﻿
namespace Station.Simulation
{
    using Mes.Simulation;
    using Opc.Ua;
    using Opc.Ua.Client;
    using Opc.Ua.Configuration;
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Runtime.Serialization;
    using System.Threading;
    using System.Threading.Tasks;

    [CollectionDataContract(Name = "ListOfStations", Namespace = Namespaces.OpcUaConfig, ItemName = "StationConfig")]
    public partial class StationsCollection : List<Station>
    {
        public StationsCollection() { }

        public static StationsCollection Load(ApplicationConfiguration configuration)
        {
            return configuration.ParseExtension<StationsCollection>();
        }
    }

    public class Program
    {
        private static Station m_station = null;
        private static SessionHandler m_sessionAssembly = null;
        private static SessionHandler m_sessionTest = null;
        private static SessionHandler m_sessionPackaging = null;

        private static Object m_mesStatusLock = new Object();

        private static StationStatus m_statusAssembly = StationStatus.Ready;
        private static StationStatus m_statusTest = StationStatus.Ready;
        private static StationStatus m_statusPackaging = StationStatus.Ready;

        private static ManualResetEvent m_quitEvent;
        private static DateTime m_lastActivity = DateTime.MinValue;

        private const int c_Assembly = 0;
        private const int c_Test = 1;
        private const int c_Packaging = 2;

        private static ulong[] m_serialNumber = { 0, 0, 0 };

        private static bool m_faultTest = false;
        private static bool m_faultPackaging = false;
        private static bool m_doneAssembly = false;
        private static bool m_doneTest = false;

        private const int c_updateRate = 1000;
        private const int c_waitTime = 60 * 1000;
        private const int c_connectTimeout = 300 * 1000;

        private static Timer m_timer = null;

        public static AzureFileStorage _storage = new AzureFileStorage();

        public static double PowerConsumption { get; set; }

        public static ulong CycleTime { get; set; }

        public static void Main()
        {
            try
            {
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("StationType")))
                {
                    throw new ArgumentException("You must specify the StationType environment variable!");
                }

                if (Environment.GetEnvironmentVariable("StationType") == "MES")
                {
                    MES();
                }
                else
                {
                    Task t = ConsoleServer();
                    t.Wait();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Exception: " + ex.Message);
            }
        }

        private static void MES()
        {
            try
            {
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ProductionLineName")))
                {
                    throw new ArgumentException("You must specify the ProductionLineName environment variable!");
                }

                ApplicationInstance.MessageDlg = new ApplicationMessageDlg();
                ApplicationInstance application = new ApplicationInstance();
                application.ApplicationName = "Manufacturing Execution System";
                application.ApplicationType = ApplicationType.Client;
                application.ConfigSectionName = "Opc.Ua.MES";

                LoadCertsFromCloud("MES." + Environment.GetEnvironmentVariable("ProductionLineName"));

                // replace the certificate subject name in the configuration
                string configFilePath = Path.Combine(Directory.GetCurrentDirectory(), application.ConfigSectionName + ".Config.xml");
                string configFileContent = File.ReadAllText(configFilePath).Replace("UndefinedMESName", "MES." + Environment.GetEnvironmentVariable("ProductionLineName"));
                File.WriteAllText(configFilePath, configFileContent);

                // load the application configuration
                ApplicationConfiguration appConfiguration = application.LoadApplicationConfiguration(false).Result;

                // check the application certificate
                bool certOK = application.CheckApplicationInstanceCertificate(false, 0).GetAwaiter().GetResult();
                if (!certOK)
                {
                    throw new Exception("Application instance certificate invalid!");
                }
                else
                {
                    StoreCertsInCloud();
                }

                // create OPC UA cert validator
                application.ApplicationConfiguration.CertificateValidator = new CertificateValidator();
                application.ApplicationConfiguration.CertificateValidator.CertificateValidation += new CertificateValidationEventHandler(OPCUAClientCertificateValidationCallback);

                // replace the production line name in the list of endpoints to connect to.
                string endpointsFilePath = Path.Combine(Directory.GetCurrentDirectory(), application.ConfigSectionName + ".Endpoints.xml");
                string endpointsFileContent = File.ReadAllText(endpointsFilePath).Replace("munich", Environment.GetEnvironmentVariable("ProductionLineName"));
                File.WriteAllText(endpointsFilePath, endpointsFileContent);

                // load list of endpoints to connect to
                ConfiguredEndpointCollection endpoints = appConfiguration.LoadCachedEndpoints(true);
                endpoints.DiscoveryUrls = appConfiguration.ClientConfiguration.WellKnownDiscoveryUrls;

                StationsCollection collection = StationsCollection.Load(appConfiguration);
                if (collection.Count > 0)
                {
                    m_station = collection[0];
                }
                else
                {
                    throw new ArgumentException("Can not load station definition from configuration file!");
                }

                // connect to all servers
                m_sessionAssembly = new SessionHandler();
                m_sessionTest = new SessionHandler();
                m_sessionPackaging = new SessionHandler();

                DateTime retryTimeout = DateTime.UtcNow + TimeSpan.FromMilliseconds(c_connectTimeout);
                do
                {
                    try
                    {
                        m_sessionAssembly.EndpointConnect(endpoints[c_Assembly], appConfiguration);
                        m_sessionTest.EndpointConnect(endpoints[c_Test], appConfiguration);
                        m_sessionPackaging.EndpointConnect(endpoints[c_Packaging], appConfiguration);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Exception connecting to assembly line: {0}, retry!", ex.Message);
                        Thread.Sleep(1000);
                    }

                    if (DateTime.UtcNow > retryTimeout)
                    {
                        throw new Exception(string.Format("Failed to connect to assembly line for {0} seconds!", c_connectTimeout / 1000));
                    }
                }
                while (!(m_sessionAssembly.SessionConnected && m_sessionTest.SessionConnected && m_sessionPackaging.SessionConnected));

                if (!CreateMonitoredItem(m_station.StatusNode, m_sessionAssembly.Session, new MonitoredItemNotificationEventHandler(MonitoredItem_AssemblyStation)))
                {
                    throw new Exception("Failed to create monitored Item for the assembly station!");
                }
                if (!CreateMonitoredItem(m_station.StatusNode, m_sessionTest.Session, new MonitoredItemNotificationEventHandler(MonitoredItem_TestStation)))
                {
                    throw new Exception("Failed to create monitored Item for the test station!");
                }
                if (!CreateMonitoredItem(m_station.StatusNode, m_sessionPackaging.Session, new MonitoredItemNotificationEventHandler(MonitoredItem_PackagingStation)))
                {
                    throw new Exception("Failed to create monitored Item for the packaging station!");
                }

                StartAssemblyLine();

                // MESLogic method is executed periodically, with period c_updateRate
                RestartTimer(c_updateRate);

                Console.WriteLine("MES started. Press Ctrl-C to exit.");

                m_quitEvent = new ManualResetEvent(false);
                try
                {
                    Console.CancelKeyPress += (sender, eArgs) => {
                        m_quitEvent.Set();
                        eArgs.Cancel = true;
                    };
                }
                catch
                {
                }

                // wait for MES timeout or Ctrl-C
                m_quitEvent.WaitOne();

            }
            catch (Exception ex)
            {
                Console.WriteLine("Critical Exception: {0}, MES exiting!", ex.Message);
            }
        }

        private static void StoreCertsInCloud()
        {
            // store app certs
            foreach (string filePath in Directory.EnumerateFiles(Path.Combine(Directory.GetCurrentDirectory(), "pki", "own", "certs"), "*.der"))
            {
                _storage.StoreFileAsync(filePath, File.ReadAllBytesAsync(filePath).GetAwaiter().GetResult()).GetAwaiter().GetResult();
            }

            // store private keys
            foreach (string filePath in Directory.EnumerateFiles(Path.Combine(Directory.GetCurrentDirectory(), "pki", "own", "private"), "*.pfx"))
            {
                _storage.StoreFileAsync(filePath, File.ReadAllBytesAsync(filePath).GetAwaiter().GetResult()).GetAwaiter().GetResult();
            }

            // store trusted certs
            foreach (string filePath in Directory.EnumerateFiles(Path.Combine(Directory.GetCurrentDirectory(), "pki", "trusted", "certs"), "*.der"))
            {
                _storage.StoreFileAsync(filePath, File.ReadAllBytesAsync(filePath).GetAwaiter().GetResult()).GetAwaiter().GetResult();
            }
        }

        private static void LoadCertsFromCloud(string appName)
        {
            try
            {
                // load app cert from storage
                string certFilePath = _storage.FindFileAsync(Path.Combine(Directory.GetCurrentDirectory(), "pki", "own", "certs"), appName).GetAwaiter().GetResult();
                byte[] certFile = _storage.LoadFileAsync(certFilePath).GetAwaiter().GetResult();
                if (certFile == null)
                {
                    Console.WriteLine("Could not load cert file, creating a new one. This means the new cert needs to be trusted by all OPC UA servers we connect to!");
                }
                else
                {
                    if (!Path.IsPathRooted(certFilePath))
                    {
                        certFilePath = Path.DirectorySeparatorChar.ToString() + certFilePath;
                    }

                    if (!Directory.Exists(Path.GetDirectoryName(certFilePath)))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(certFilePath));
                    }

                    File.WriteAllBytes(certFilePath, certFile);
                }

                // load app private key from storage
                string keyFilePath = _storage.FindFileAsync(Path.Combine(Directory.GetCurrentDirectory(), "pki", "own", "private"), appName).GetAwaiter().GetResult();
                byte[] keyFile = _storage.LoadFileAsync(keyFilePath).GetAwaiter().GetResult();
                if (keyFile == null)
                {
                   Console.WriteLine("Could not load key file, creating a new one. This means the new cert generated from the key needs to be trusted by all OPC UA servers we connect to!");
                }
                else
                {
                    if (!Path.IsPathRooted(keyFilePath))
                    {
                        keyFilePath = Path.DirectorySeparatorChar.ToString() + keyFilePath;
                    }

                    if (!Directory.Exists(Path.GetDirectoryName(keyFilePath)))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(keyFilePath));
                    }

                    File.WriteAllBytes(keyFilePath, keyFile);
                }

                // load trusted certs
                string[] trustedcertFilePath = _storage.FindFilesAsync(Path.Combine(Directory.GetCurrentDirectory(), "pki", "trusted", "certs")).GetAwaiter().GetResult();
                if (trustedcertFilePath != null)
                {
                    foreach (string filePath in trustedcertFilePath)
                    {
                        byte[] trustedcertFile = _storage.LoadFileAsync(filePath).GetAwaiter().GetResult();
                        if (trustedcertFile == null)
                        {
                            Console.WriteLine("Could not load trusted cert file " + filePath);
                        }
                        else
                        {
                            string localFilePath = filePath;

                            if (!Path.IsPathRooted(localFilePath))
                            {
                                localFilePath = Path.DirectorySeparatorChar.ToString() + localFilePath;
                            }

                            if (!Directory.Exists(Path.GetDirectoryName(localFilePath)))
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(localFilePath));
                            }

                            File.WriteAllBytes(localFilePath, trustedcertFile);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Cloud not load cert or private key files, creating a new ones. This means the new cert needs to be trusted by all OPC UA servers we connect to!: " + ex.Message);
            }
        }

        private static void MesLogic(object state)
        {
            try
            {
                lock (m_mesStatusLock)
                {

                    // when the assembly station is done and the test station is ready
                    // move the serial number (the product) to the test station and call
                    // the method execute for the test station to start working, and
                    // the reset method for the assembly to go in the ready state
                    if ((m_doneAssembly) && (m_statusTest == StationStatus.Ready))
                    {
                        Console.WriteLine("#{0} Assembly --> Test", m_serialNumber[c_Assembly]);
                        m_serialNumber[c_Test] = m_serialNumber[c_Assembly];
                        m_sessionTest.Session.Call(m_station.RootMethodNode, m_station.ExecuteMethodNode, m_serialNumber[c_Test]);
                        m_sessionAssembly.Session.Call(m_station.RootMethodNode, m_station.ResetMethodNode, null);
                        m_lastActivity = DateTime.UtcNow;
                        m_doneAssembly = false;
                    }

                    // when the test station is done and the packaging station is ready
                    // move the serial number (the product) to the packaging station and call
                    // the method execute for the packaging station to start working, and
                    // the reset method for the test to go in the ready state
                    if ((m_doneTest) && (m_statusPackaging == StationStatus.Ready))
                    {
                        Console.WriteLine("#{0} Test --> Packaging", m_serialNumber[c_Test]);
                        m_serialNumber[c_Packaging] = m_serialNumber[c_Test];
                        m_sessionPackaging.Session.Call(m_station.RootMethodNode, m_station.ExecuteMethodNode, m_serialNumber[c_Packaging]);
                        m_sessionTest.Session.Call(m_station.RootMethodNode, m_station.ResetMethodNode, null);
                        m_lastActivity = DateTime.UtcNow;
                        m_doneTest = false;
                    }

                    if (m_lastActivity + TimeSpan.FromMilliseconds(c_connectTimeout) < DateTime.UtcNow)
                    {
                        // recover from network / communication outages and restart assembly line
                        Console.WriteLine("MES activity timeout - restart the MES controller.");
                        m_quitEvent.Set();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("MES logic exception: {0}!", ex.Message);
            }
            finally
            {
                // reschedule the timer event
                RestartTimer(c_updateRate);
            }
        }

        private static bool CreateMonitoredItem(NodeId nodeId, Session session, MonitoredItemNotificationEventHandler handler)
        {
            if (session != null)
            {
                // access the default subscription, add it to the session and only create it if successful
                Subscription subscription = session.DefaultSubscription;
                if (session.AddSubscription(subscription))
                {
                    subscription.Create();
                }

                // add the new monitored item.
                MonitoredItem monitoredItem = new MonitoredItem(subscription.DefaultItem);
                if (monitoredItem != null)
                {
                    // Set monitored item attributes
                    // StartNodeId = NodeId to be monitored
                    // AttributeId = which attribute of the node to monitor (in this case the value)
                    // MonitoringMode = When sampling is enabled, the Server samples the item.
                    // In addition, each sample is evaluated to determine if
                    // a Notification should be generated. If so, the
                    // Notification is queued. If reporting is enabled,
                    // the queue is made available to the Subscription for transfer
                    monitoredItem.StartNodeId = nodeId;
                    monitoredItem.AttributeId = Attributes.Value;
                    monitoredItem.DisplayName = nodeId.Identifier.ToString();
                    monitoredItem.MonitoringMode = MonitoringMode.Reporting;
                    monitoredItem.SamplingInterval = 0;
                    monitoredItem.QueueSize = 0;
                    monitoredItem.DiscardOldest = true;

                    monitoredItem.Notification += handler;
                    subscription.AddItem(monitoredItem);
                    subscription.ApplyChanges();

                    return true;
                }
                else
                {
                    Console.WriteLine("Error: Can not create monitored item!");
                }
            }
            else
            {
                Console.WriteLine("Argument error: Session is null!");
            }

            return false;
        }

        private static void StartAssemblyLine()
        {
            lock (m_mesStatusLock)
            {
                m_doneAssembly = false;
                m_doneTest = false;

                m_serialNumber[c_Assembly]++;

                Console.WriteLine("<<Assembly line reset!>>");

                // reset assembly line
                m_sessionAssembly.Session.Call(m_station.RootMethodNode, m_station.ResetMethodNode, null);
                m_sessionTest.Session.Call(m_station.RootMethodNode, m_station.ResetMethodNode, null);
                m_sessionPackaging.Session.Call(m_station.RootMethodNode, m_station.ResetMethodNode, null);

                // update status
                m_statusAssembly = (StationStatus)m_sessionAssembly.Session.ReadValue(m_station.StatusNode).Value;
                m_statusTest = (StationStatus)m_sessionTest.Session.ReadValue(m_station.StatusNode).Value;
                m_statusPackaging = (StationStatus)m_sessionPackaging.Session.ReadValue(m_station.StatusNode).Value;

                Console.WriteLine("#{0} Assemble ", m_serialNumber[c_Assembly]);
                // start assembly
                m_sessionAssembly.Session.Call(m_station.RootMethodNode, m_station.ExecuteMethodNode, m_serialNumber[c_Assembly]);

                // reset communication timeout
                m_lastActivity = DateTime.UtcNow;

            }
        }

        private static void MonitoredItem_AssemblyStation(MonitoredItem monitoredItem, MonitoredItemNotificationEventArgs e)
        {

            try
            {
                lock (m_mesStatusLock)
                {
                    MonitoredItemNotification change = e.NotificationValue as MonitoredItemNotification;
                    m_statusAssembly = (StationStatus)change.Value.Value;

                    Console.WriteLine("-AssemblyStation: {0}", m_statusAssembly);

                    // now check what the status is
                    switch (m_statusAssembly)
                    {
                        case StationStatus.Ready:
                            if ((!m_faultTest) || (!m_faultPackaging))
                            {
                                // build the next product by calling execute with new serial number
                                m_serialNumber[c_Assembly]++;
                                Console.WriteLine("#{0} Assemble ", m_serialNumber[c_Assembly]);
                                m_sessionAssembly.Session.Call(m_station.RootMethodNode, m_station.ExecuteMethodNode, m_serialNumber[c_Assembly]);
                            }
                            break;

                        case StationStatus.WorkInProgress:
                            // nothing to do
                            break;

                        case StationStatus.Done:
                            m_doneAssembly = true;
                            break;

                        case StationStatus.Discarded:
                            // product was automatically discarded by the station, reset
                            Console.WriteLine("#{0} Discarded in Assembly", m_serialNumber[c_Assembly]);
                            m_sessionAssembly.Session.Call(m_station.RootMethodNode, m_station.ResetMethodNode, null);
                            break;

                        case StationStatus.Fault:
                            Task.Run(async () =>
                            {
                                // station is at fault state, wait some time to simulate manual intervention before reseting
                                Console.WriteLine("<<AssemblyStation: Fault>>");
                                await Task.Delay(c_waitTime);
                                Console.WriteLine("<<AssemblyStation: Restart from Fault>>");

                                m_sessionAssembly.Session.Call(m_station.RootMethodNode, m_station.ResetMethodNode, null);
                            });
                            break;

                        default:
                            Console.WriteLine("Argument error: Invalid station status type received!");
                            break;
                    }
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine("Exception: Error processing monitored item notification: " + exception.Message);
            }
        }

        private static void MonitoredItem_TestStation(MonitoredItem monitoredItem, MonitoredItemNotificationEventArgs e)
        {

            try
            {
                lock (m_mesStatusLock)
                {
                    MonitoredItemNotification change = e.NotificationValue as MonitoredItemNotification;
                    m_statusTest = (StationStatus)change.Value.Value;

                    Console.WriteLine("--TestStation: {0}", m_statusTest);

                    switch (m_statusTest)
                    {
                        case StationStatus.Ready:
                            // nothing to do
                            break;

                        case StationStatus.WorkInProgress:
                            // nothing to do
                            break;

                        case StationStatus.Done:
                            Console.WriteLine("#{0} Tested, Passed", m_serialNumber[c_Test]);
                            m_doneTest = true;
                            break;

                        case StationStatus.Discarded:
                            Console.WriteLine("#{0} Tested, not Passed, Discarded", m_serialNumber[c_Test]);
                            m_sessionTest.Session.Call(m_station.RootMethodNode, m_station.ResetMethodNode, null);
                            break;

                        case StationStatus.Fault:
                            {
                                m_faultTest = true;
                                Task.Run(async () =>
                                {
                                    Console.WriteLine("<<TestStation: Fault>>");
                                    await Task.Delay(c_waitTime);
                                    Console.WriteLine("<<TestStation: Restart from Fault>>");

                                    m_faultTest = false;
                                    m_sessionTest.Session.Call(m_station.RootMethodNode, m_station.ResetMethodNode, null);
                                });
                            }
                            break;

                        default:
                            {
                                Console.WriteLine("Argument error: Invalid station status type received!");
                                return;
                            }
                    }
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine("Exception: Error processing monitored item notification: " + exception.Message);
            }
        }

        private static void MonitoredItem_PackagingStation(MonitoredItem monitoredItem, MonitoredItemNotificationEventArgs e)
        {
            try
            {
                lock (m_mesStatusLock)
                {
                    MonitoredItemNotification change = e.NotificationValue as MonitoredItemNotification;
                    m_statusPackaging = (StationStatus)change.Value.Value;

                    Console.WriteLine("---PackagingStation: {0}", m_statusPackaging);

                    switch (m_statusPackaging)
                    {
                        case StationStatus.Ready:
                            // nothing to do
                            break;

                        case StationStatus.WorkInProgress:
                            // nothing to do
                            break;

                        case StationStatus.Done:
                            Console.WriteLine("#{0} Packaged", m_serialNumber[c_Packaging]);
                            // last station (packaging) is done, reset so the next product can be built
                            m_sessionPackaging.Session.Call(m_station.RootMethodNode, m_station.ResetMethodNode, null);
                            break;

                        case StationStatus.Discarded:
                            Console.WriteLine("#{0} Discarded in Packaging", m_serialNumber[c_Packaging]);
                            m_sessionPackaging.Session.Call(m_station.RootMethodNode, m_station.ResetMethodNode, null);
                            break;

                        case StationStatus.Fault:
                            {
                                m_faultPackaging = true;
                                Task.Run(async () =>
                                {
                                    Console.WriteLine("<<PackagingStation: Fault>>");
                                    await Task.Delay(c_waitTime);
                                    Console.WriteLine("<<PackagingStation: Restart from Fault>>");

                                    m_faultPackaging = false;
                                    m_sessionPackaging.Session.Call(m_station.RootMethodNode, m_station.ResetMethodNode, null);
                                });
                            }
                            break;

                        default:
                            Console.WriteLine("Argument error: Invalid station status type received!");
                            break;
                    }
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine("Exception: Error processing monitored item notification: " + exception.Message);
            }
        }

        private static void RestartTimer(int dueTime)
        {
            if (m_timer != null)
            {
                m_timer.Dispose();
            }

            m_timer = new Timer(MesLogic, null, dueTime, Timeout.Infinite);
        }

        private static async Task ConsoleServer()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("StationURI")))
            {
                throw new ArgumentException("You must specify the StationURI environment variable!");
            }

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PowerConsumption")))
            {
                throw new ArgumentException("You must specify the PowerConsumption environment variable!");
            }

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CycleTime")))
            {
                throw new ArgumentException("You must specify the CycleTime environment variable!");
            }

            ApplicationInstance.MessageDlg = new ApplicationMessageDlg();
            ApplicationInstance application = new ApplicationInstance();

            Uri stationUri = new Uri(Environment.GetEnvironmentVariable("StationURI"));

            application.ApplicationName = stationUri.DnsSafeHost.ToLowerInvariant();
            application.ConfigSectionName = "Opc.Ua.Station";
            application.ApplicationType = ApplicationType.Server;

            LoadCertsFromCloud(application.ApplicationName);

            // replace the certificate subject name in the configuration
            string configFilePath = Path.Combine(Directory.GetCurrentDirectory(), application.ConfigSectionName + ".Config.xml");
            string configFileContent = File.ReadAllText(configFilePath).Replace("UndefinedStationName", application.ApplicationName);
            File.WriteAllText(configFilePath, configFileContent);

            // load the application configuration.
            ApplicationConfiguration config = await application.LoadApplicationConfiguration(false);
            if (config == null)
            {
                throw new Exception("Application configuration is null!");
            }

            // calculate our power consumption in [kW] and cycle time in [s]
            PowerConsumption = ulong.Parse(Environment.GetEnvironmentVariable("PowerConsumption"), NumberStyles.Integer);
            CycleTime = ulong.Parse(Environment.GetEnvironmentVariable("CycleTime"), NumberStyles.Integer);

            // print out our configuration
            Console.WriteLine("OPC UA Server Configuration:");
            Console.WriteLine("----------------------------");
            Console.WriteLine("OPC UA Endpoint: " + config.ServerConfiguration.BaseAddresses[0].ToString());
            Console.WriteLine("Application URI: " + config.ApplicationUri);
            Console.WriteLine("Power consumption: " + PowerConsumption.ToString() + "kW");
            Console.WriteLine("Cycle time: " + CycleTime.ToString() + "s");

            // load the application configuration
            ApplicationConfiguration appConfiguration = application.LoadApplicationConfiguration(false).Result;

            // check the application certificate
            bool certOK = await application.CheckApplicationInstanceCertificate(false, 0).ConfigureAwait(false);
            if (!certOK)
            {
                throw new Exception("Application instance certificate invalid!");
            }
            else
            {
                StoreCertsInCloud();
            }

#if DEBUG
            // create OPC UA cert validator
            application.ApplicationConfiguration.CertificateValidator = new CertificateValidator();
            application.ApplicationConfiguration.CertificateValidator.CertificateValidation += new CertificateValidationEventHandler(OPCUAClientCertificateValidationCallback);
#endif
            // start the server.
            await application.Start(new FactoryStationServer());

            Console.WriteLine("Server started. Press any key to exit.");

            try
            {
                Console.ReadKey(true);
            }
            catch (Exception)
            {
                // wait forever if there is no console
                Thread.Sleep(Timeout.Infinite);
            }
        }

        private static void OPCUAClientCertificateValidationCallback(CertificateValidator sender, CertificateValidationEventArgs e)
        {
            // always trust the OPC UA client certificate ind debug mode
            if (e.Error.StatusCode == StatusCodes.BadCertificateUntrusted)
            {
                e.Accept = true;
            }
        }
    }
}
