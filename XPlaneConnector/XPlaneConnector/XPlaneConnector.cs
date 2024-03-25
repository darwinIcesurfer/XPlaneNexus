﻿namespace XPlaneConnector;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection.Metadata;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


public class XPlaneConnector : IDisposable
{
    private const int CHECKINTERVAL_MILLISECONDS = 1000;
    private const int START_INDEX_VALUE_PAIRS = 5;
    private const int BYTES_PER_INDEX_OR_FlOAT = 4;
    private TimeSpan MaxDataRefAge = TimeSpan.FromSeconds(5);

    private CultureInfo EnCulture = new CultureInfo("en-US");
    public event Action<byte[]> OnRawReceive;
    private UdpClient? _client;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _receiveDatagramsTask;
    private Task? _requestDatarefsTask;
    private IPEndPoint XPlaneEP;

    public delegate void DataRefReceived(DataRefElement dataRef);
    public event DataRefReceived? OnDataRefReceived;
    public delegate void LogHandler(string message);
    private Dictionary<int, DataRefElement> DataRefs;

    public DateTime LastReceive { get; internal set; }
    public IEnumerable<byte> LastBuffer { get; internal set; } = new byte[0];

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="ip">IP of the machine running X-Plane, default 127.0.0.1 (localhost)</param>
    /// <param name="xplanePort">Port the machine running X-Plane is listening for, default 49000</param>
    public XPlaneConnector(string ip = "127.0.0.1", int xplanePort = 49000)
    {
        XPlaneEP = new IPEndPoint(IPAddress.Parse(ip), xplanePort);
        DataRefs = new Dictionary<int, DataRefElement>();
        OnRawReceive += ParseResponse;
    }


    /// <summary>
    /// Initialize the communication with X-Plane machine and starts listening for DataRefs
    /// </summary>
    public async Task Start()
    {
        await StartSendAndReceiveAsync();
    }

    private async Task StartSendAndReceiveAsync()
    {
        try
        {

            _client = new UdpClient(AddressFamily.InterNetwork);
            _client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

            _cancellationTokenSource = new CancellationTokenSource();

            // Start both the receiving and request for Datarefs tasks
            _receiveDatagramsTask = Task.Run(ReceiveXplaneDatagramsAsync);
            _requestDatarefsTask = Task.Run(RequestXplaneDatarefsAsync);

            // Wait for both tasks to complete
            await Task.WhenAll(_receiveDatagramsTask, _requestDatarefsTask);

            // Cleanup resources
            _client.Close();

        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.ToString());
            throw;
        }
    }

    private async Task ReceiveXplaneDatagramsAsync()
    {
        try
        {
            if (_client == null)
                throw new Exception("UDP client is null in ReceiveXplaneDatagramsAsync method");

            // if _cancellationTokenSource is null, the following expression will evaluate to true and exit the loop
            while (!(_cancellationTokenSource?.Token.IsCancellationRequested ?? true))
            {
                //Debug.Print("awaiting response");
                var response = await _client.ReceiveAsync().ConfigureAwait(false);
                //Debug.Print(Encoding.ASCII.GetString(response.Buffer));
                OnRawReceive?.Invoke(response.Buffer);
            }
            if (_client != null)
                _client.Close();
        }
        catch (Exception ex)
        {
            Debug.Print("Error: ", ex.ToString());
            throw;
        }
    }

    /// <summary>
    ///Every CHECKINTERVAL_MILLISECONDS go through the datarefs List and if data has not been received
    ///recently,  send a new request to XPlane to start sending data over udp
    /// </summary>
    /// <returns></returns>
    private async Task RequestXplaneDatarefsAsync()
    {
        try
        {
            // if _cancellationTokenSource is null, the following expression will evaluate to true and exit the loop
            while (!(_cancellationTokenSource?.Token.IsCancellationRequested ?? true))
            {
                foreach (KeyValuePair<int, DataRefElement> pair in DataRefs)
                {
                    if (pair.Value.Age > MaxDataRefAge)
                    {
                        RequestDataRef(pair.Value);
                    }
                }
                await Task.Delay(CHECKINTERVAL_MILLISECONDS).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Debug.Print("Error: ", ex.ToString());
            throw;
        }
    }

    /// <summary>
    /// Stops the communications with the X-Plane machine
    /// </summary>
    /// <param name="timeout"></param>
    public void Stop(int timeout = 5000)
    {
        try
        {
            if (_client == null)
                throw new Exception("no UDP client available to stop existing XPlane datarefs");

            // unsubscribe from all datarefs
            foreach (KeyValuePair<int, DataRefElement> pair in DataRefs)
            {
                if (pair.Value != null)
                    if (pair.Value.DataRefPath != null)
                        Unsubscribe(pair.Value.DataRefPath);
            }

            // Create a list of all the nonNullTasks
            List<Task> nonNullTasks = new List<Task>();
            if (_receiveDatagramsTask != null)
                nonNullTasks.Add(_receiveDatagramsTask);
            if (_requestDatarefsTask != null)
                nonNullTasks.Add(_requestDatarefsTask);

            // Cancel all the nonNull tasks
            if (_cancellationTokenSource != null)
            {
                _cancellationTokenSource?.Cancel();
                var IsAllTasksClosed = Task.WaitAll(nonNullTasks.ToArray(), timeout);
                if (!IsAllTasksClosed)
                    throw new Exception(String.Format("Tasks were not all closed within {0}ms", timeout));
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                _client.Close();

            }
        }
        catch (Exception ex)
        {
            Debug.Print("Error: {0}", ex.ToString());
            throw;
        }
    }
    private void ParseResponse(byte[] buffer)
    {
        var pos = 0;
        var header = Encoding.UTF8.GetString(buffer, pos, 4);

        if (header == "RREF") // Ignore other messages
        {
            pos += START_INDEX_VALUE_PAIRS;
            while (pos < buffer.Length)
            {
                try
                {
                    var id = BitConverter.ToInt32(buffer, pos);
                    pos += BYTES_PER_INDEX_OR_FlOAT;
                    var value = BitConverter.ToSingle(buffer, pos);
                    pos += BYTES_PER_INDEX_OR_FlOAT;
                    if (!DataRefs.ContainsKey(id))
                        throw new ArgumentException(String.Format("key {0} not found in Datarefs", id));
    
                    DataRefs[id].Update(value);
                    OnDataRefReceived?.Invoke(DataRefs[id]);
                }
                catch (Exception ex)
                {
                    Debug.Print("Error: {0} ", ex.ToString());
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// Sends a command
    /// </summary>
    /// <param name="command">Command to send</param>
    public void SendCommand(XPlaneCommand command)
    {
        if (command == null)
            throw new ArgumentNullException(nameof(command));

        if (_client == null)
            throw new Exception("_client is null in SendCommand Method");

        var dg = new XPDatagram();
        dg.Add("CMND");
        dg.Add(command.Command);

        _client.Send(dg.Get(), dg.Len);
    }

    /// <summary>
    /// Sends a command continuously. Use return parameter to cancel the send cycle
    /// </summary>
    /// <param name="command">Command to send</param>
    /// <returns>Token to cancel the executing</returns>
    public CancellationTokenSource StartCommand(XPlaneCommand command)
    {
        var tokenSource = new CancellationTokenSource();

        Task.Run(() =>
        {
            while (!tokenSource.IsCancellationRequested)
            {
                SendCommand(command);
            }
        }, tokenSource.Token);

        return tokenSource;
    }

    public void StopCommand(CancellationTokenSource token)
    {
        token.Cancel();
    }

    /// <summary>
    /// Subscribe to a DataRef, notification will be sent every time the value changes
    /// </summary>
    /// <param name="dataref">DataRef to subscribe to</param>
    /// <param name="frequency">Times per seconds X-Plane will be sending this value</param>
    /// <param name="onchange">Callback invoked every time a change in the value is detected</param>
    public void Subscribe(DataRefElement dataref, int frequency, Action<DataRefElement, float> onchange)
    {
        if (dataref == null)
            throw new ArgumentNullException(nameof(dataref));

        if (onchange != null)
            dataref.OnValueChange += (e, v) => { onchange(e, v); };

        if (frequency > 0)
            dataref.Frequency = frequency;

        // The index within the dataref will be used as the dictionary key as they both require a unique value
        DataRefs[dataref.Id] = dataref;
    }

    /// <summary>
    /// String datarefs require a 'regular' subscription to each element of the string to be returned.  Once all the 
    /// elements have been received and decoded into a string, then the onchange notification will be emitted.
    /// </summary>
    /// <param name="dataref">DataRef to subscribe to</param>
    /// <param name="frequency">Times per seconds X-Plane will be sending this value</param>
    /// <param name="onchange">Callback invoked every time a change in the full string is detected</param>
    public void Subscribe(StringDataRefElement dataref, int frequency, Action<StringDataRefElement, string> onchange)
    {
        if (dataref == null)
            throw new ArgumentNullException(nameof(dataref));

        dataref.OnValueChange += (e, v) => { onchange(e, v); };

        // Create individual subscriptions for each character of a string.  Each character gets its own datarefElement and associated
        // index when the new DataRefElement is added to the DataRefs List
        for (var c = 0; c < dataref.StringLength; c++)
        {
            var arrayElementDataRef = new DataRefElement
            {
                DataRefPath = $"{dataref.DataRef}[{c}]",
                Description = ""
            };

            var currentIndex = c;
            Subscribe(arrayElementDataRef, frequency, (e, v) =>
            {
                dataref.Update(currentIndex, v);
            });
        }
    }


    /// <summary>
    /// Send a request to the XPlane at the _XPlaneEP endpoint to begin broadcasting data for the specified dataref
    /// XPlane will respond by sending the data back to the IP and Port that sent the request
    /// </summary>
    /// <param name="element"></param>
    private void RequestDataRef(DataRefElement element)
    {
        try
        {
            if (element == null)
                throw new ArgumentException("dataRefElement not provided in RequestDataRef method");
            if (element.DataRefPath == null)
                throw new ArgumentException("Dataref path string not provided in RequestDataRef method");

            if (_client != null)
            {
                var dg = new XPDatagram();
                dg.Add("RREF");
                dg.Add(element.Frequency);
                dg.Add(element.Id);
                dg.Add(element.DataRefPath);
                dg.FillTo(413);

                _client.Send(dg.Get(), dg.Len, XPlaneEP);
            }
        }
        catch (Exception ex)
        {
            Debug.Print("Error in RequestDataRef method {0}", ex.ToString());
            throw;
        }
    }

    /// <summary>
    /// Informs X-Plane to stop sending this DataRef element
    /// </summary>
    /// <param name="datarefPath">Individual DataRef to unsubscribe to including the [index] if it is used</param>
    public void Unsubscribe(string datarefPath)
    {
        try
        {
            if (!DataRefs.Any(pair => pair.Value.DataRefPath == datarefPath))
                throw new ArgumentException(String.Format("No element in DataRefs matching {0} was found", datarefPath));

            var key = DataRefs.First(pair => pair.Value.DataRefPath == datarefPath).Key;

            var dataref = DataRefs[key];

            // prepare the datagram to inform XPlane that data for this dataref should no longer be sent
            var dg = new XPDatagram();
            dg.Add("RREF");
            dg.Add(dataref.Id);
            dg.Add(0);
            dg.Add(datarefPath);
            dg.FillTo(413);

            _client?.Send(dg.Get(), dg.Len);

            // Remove the dataref from the Dictionary
            DataRefs.Remove(key);
        }
        catch (Exception ex)
        {
            Debug.Print("Error: {0}", ex.ToString());
            throw;
        }
    }

    /// <summary>
    /// Informs X-Plane to change the value of the DataRef
    /// </summary>
    /// <param name="dataref">DataRef that will be changed</param>
    /// <param name="value">New value of the DataRef</param>
    public void SetDataRefValue(DataRefElement dataref, float value)
    {
        if (dataref == null)
            throw new ArgumentNullException(nameof(dataref));
        if (dataref.DataRefPath == null)
            throw new ArgumentNullException(nameof(dataref.DataRefPath));

        SetDataRefValue(dataref.DataRefPath, value);
    }

    /// <summary>
    /// Informs X-Plane to change the value of the DataRef
    /// </summary>
    /// <param name="dataref">DataRef that will be changed</param>
    /// <param name="value">New value of the DataRef</param>
    public void SetDataRefValue(string dataref, float value)
    {
        var dg = new XPDatagram();
        dg.Add("DREF");
        dg.Add(value);
        dg.Add(dataref);
        dg.FillTo(509);

        _client?.Send(dg.Get(), dg.Len);
    }
    /// <summary>
    /// Informs X-Plane to change the value of the DataRef
    /// </summary>
    /// <param name="dataref">DataRef that will be changed</param>
    /// <param name="value">New value of the DataRef</param>
    public void SetDataRefValue(string dataref, string value)
    {
        var dg = new XPDatagram();
        dg.Add("DREF");
        dg.Add(value);
        dg.Add(dataref);
        dg.FillTo(509);

        _client?.Send(dg.Get(), dg.Len);
    }

    /// <summary>
    /// Request X-Plane to close, a notification message will appear
    /// </summary>
    public void QuitXPlane()
    {
        var dg = new XPDatagram();
        dg.Add("QUIT");

        _client?.Send(dg.Get(), dg.Len);
    }

    /// <summary>
    /// Inform X-Plane that a system is failed
    /// </summary>
    /// <param name="system">Integer value representing the system to fail</param>
    public void Fail(int system)
    {
        var dg = new XPDatagram();
        dg.Add("FAIL");

        dg.Add(system.ToString(EnCulture));

        _client?.Send(dg.Get(), dg.Len);
    }

    /// <summary>
    /// Inform X-Plane that a system is back to normal functioning
    /// </summary>
    /// <param name="system">Integer value representing the system to recover</param>
    public void Recover(int system)
    {
        var dg = new XPDatagram();
        dg.Add("RECO");

        dg.Add(system.ToString(EnCulture));

        _client?.Send(dg.Get(), dg.Len);
    }

    protected virtual void Dispose(bool a)
    {
        _client?.Dispose();
        _cancellationTokenSource?.Dispose();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}