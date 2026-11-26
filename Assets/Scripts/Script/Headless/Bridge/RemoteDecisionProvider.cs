using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// A seat driven by a Python process over TCP loopback, one NDJSON object per line.
///
/// Why Python is the server and Unity the client: the agent is expensive to start (torch,
/// network checkpoint, card pool) while Unity tears down and rebuilds the battle scene
/// once per game. Making the long-lived side listen means the agent is paid for once per
/// experiment instead of once per game.
///
/// Threading. Every request is strictly request-then-response, and only one request is ever
/// outstanding per connection - the game is single-threaded, so that invariant holds by
/// construction. It is enforced anyway with a lock, because the main-phase decision runs on
/// a worker thread while effect-level decisions run on Unity's main thread, and the two must
/// not interleave on the same socket.
///
/// Failure policy: on timeout or a dead socket the provider returns -1, which means
/// "declined". The caller then falls back to DCGO's built-in behaviour and the game
/// continues. A crashed agent must degrade an experiment, not hang the harness - the
/// fallbacks are counted and reported so a degraded run is never mistaken for a clean one.
/// </summary>
public sealed class RemoteDecisionProvider : IDecisionProvider
{
    readonly string _host;
    readonly int _port;
    readonly float _timeoutSeconds;
    readonly object _gate = new object();

    TcpClient _client;
    Stream _stream;
    StringBuilder _readBuffer = new StringBuilder(4096);
    byte[] _chunk = new byte[8192];

    // In-flight asynchronous decision.
    Thread _worker;
    volatile bool _hasResult;
    volatile int _result = -1;
    volatile bool _cancelled;

    public string Name { get; private set; }

    /// <summary>Number of times a decision fell back to the built-in path.</summary>
    public int FallbackCount { get; private set; }

    public RemoteDecisionProvider(string host, int port, float timeoutSeconds, string name)
    {
        _host = host;
        _port = port;
        _timeoutSeconds = timeoutSeconds;
        Name = name;
    }

    #region lifecycle

    public void OnGameStart(int seatPlayerId, string seatName)
    {
        var w = new BridgeJson.Writer();
        w.Str("type", "game_start");
        w.Num("seat", seatPlayerId);
        w.Str("seat_name", seatName);
        // long, nie int: w trybie GUI ziarno pochodzi z RandomUtility.GetSecureRandom() i ma
        // pelne 64 bity - rzutowanie na int obcinalo je do wartosci, z ktorej nie da sie
        // odtworzyc partii.
        w.Num("seed", HeadlessConfig.CurrentSeed);
        SendNotification(w.Close());
    }

    public void OnGameEnd(int winnerPlayerId, string reason)
    {
        CancelChoose();

        var w = new BridgeJson.Writer();
        w.Str("type", "game_end");
        w.Num("winner", winnerPlayerId);
        w.Str("reason", reason);
        SendNotification(w.Close());
    }

    public void Close()
    {
        CancelChoose();

        lock (_gate)
        {
            CloseConnectionLocked();
        }
    }

    #endregion

    #region synchronous decisions

    public int Choose(DecisionRequest request)
    {
        string message = BuildRequest(request);

        lock (_gate)
        {
            return ExchangeLocked(message, request.Options.Count);
        }
    }

    #endregion

    #region asynchronous decisions

    public void BeginChoose(DecisionRequest request)
    {
        CancelChoose();

        string message = BuildRequest(request);
        int optionCount = request.Options.Count;

        _hasResult = false;
        _result = -1;
        _cancelled = false;

        _worker = new Thread(() =>
        {
            int answer;

            try
            {
                lock (_gate)
                {
                    answer = ExchangeLocked(message, optionCount);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Bridge] {Name}: decision thread failed: {e.Message}");
                answer = -1;
            }

            if (!_cancelled)
            {
                _result = answer;
                _hasResult = true;
            }
        });

        _worker.IsBackground = true;
        _worker.Start();
    }

    public bool TryGetChoice(out int index)
    {
        if (_hasResult)
        {
            index = _result;
            return true;
        }

        index = -1;
        return false;
    }

    public void CancelChoose()
    {
        _cancelled = true;
        _hasResult = false;

        Thread worker = _worker;
        _worker = null;

        if (worker != null && worker.IsAlive)
        {
            // Deliberately not aborting the thread: it is blocked on a socket read that the
            // timeout will release. Aborting mid-read would leave the stream at an unknown
            // offset and desynchronise every later request.
            worker.Join(TimeSpan.FromSeconds(0.05));
        }
    }

    #endregion

    #region protocol

    string BuildRequest(DecisionRequest request)
    {
        var options = new List<string>(request.Options.Count);

        foreach (DecisionOption option in request.Options)
        {
            options.Add(option.ToJson());
        }

        var w = new BridgeJson.Writer();
        w.Str("type", "decide");
        w.Str("kind", request.Kind.ToString());
        w.Str("context", request.Context);
        w.Num("seat", request.Seat != null ? request.Seat.PlayerID : -1);
        w.Num("pick", request.PickCount);
        if (request.SourceCardId != null) w.Str("source", request.SourceCardId);
        if (request.EffectName != null) w.Str("effect", request.EffectName);
        if (request.MaxPicks > 1)
        {
            // One pick of a subset selection (praca-v1.2): how far along it is.
            w.Num("picked_so_far", request.PickedSoFar);
            w.Num("max_picks", request.MaxPicks);
        }
        w.Array("options", options);

        // The observation goes with every request rather than being cached on the far side:
        // effects mutate the board between two decisions inside one turn, so a cached view
        // would be wrong exactly when a decision is most delicate.
        w.Raw("observation", ObservationSerializer.Build(
            GManager.instance != null ? GManager.instance.turnStateMachine : null,
            request.Seat));

        return w.Close();
    }

    int ExchangeLocked(string message, int optionCount)
    {
        if (!EnsureConnectedLocked())
        {
            return Decline("no connection");
        }

        try
        {
            WriteLineLocked(message);

            string reply = ReadLineLocked();

            if (reply == null)
            {
                return Decline("connection closed");
            }

            var obj = BridgeJson.Parse(reply) as Dictionary<string, object>;

            if (obj == null)
            {
                return Decline("reply is not an object");
            }

            string error = BridgeJson.GetString(obj, "error");

            if (error != null)
            {
                return Decline("agent error: " + error);
            }

            int index = BridgeJson.GetInt(obj, "index", -1);

            if (index < 0 || index >= optionCount)
            {
                // Out-of-range is treated as a decline rather than clamped. Clamping would
                // turn an agent bug into a quietly different game, which is precisely the
                // failure mode this whole harness exists to avoid.
                return Decline($"index {index} outside 0..{optionCount - 1}");
            }

            return index;
        }
        catch (Exception e)
        {
            CloseConnectionLocked();
            return Decline(e.Message);
        }
    }

    int Decline(string reason)
    {
        FallbackCount++;
        Debug.LogWarning($"[Bridge] {Name}: falling back to built-in behaviour ({reason})");
        return -1;
    }

    void SendNotification(string message)
    {
        lock (_gate)
        {
            if (!EnsureConnectedLocked())
            {
                // Loud on purpose. A dropped game_start leaves the agent unaware that a game
                // is running, and the first decision then arrives without any context. It used
                // to return in silence, so the only symptom was a crash on the Python side one
                // decision later - with nothing in the Unity console pointing at the cause.
                Debug.LogWarning($"[Bridge] {Name}: notification dropped, no server on " +
                                 $"{_host}:{_port}. Start the agent BEFORE beginning the game.");
                return;
            }

            try
            {
                WriteLineLocked(message);

                // Notifications are acknowledged too. Without an ack the next decision could
                // read the ack as its own answer, and the two would stay one message apart
                // for the rest of the game.
                ReadLineLocked();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Bridge] {Name}: notification failed: {e.Message}");
                CloseConnectionLocked();
            }
        }
    }

    #endregion

    #region socket

    bool EnsureConnectedLocked()
    {
        if (_client != null && _client.Connected)
        {
            return true;
        }

        CloseConnectionLocked();

        try
        {
            _client = new TcpClient();
            _client.NoDelay = true;
            _client.Connect(_host, _port);

            int timeoutMs = Mathf.Max(1000, (int)(_timeoutSeconds * 1000f));
            _client.ReceiveTimeout = timeoutMs;
            _client.SendTimeout = timeoutMs;

            _stream = _client.GetStream();
            _readBuffer.Length = 0;

            Debug.Log($"[Bridge] {Name}: connected to {_host}:{_port}");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Bridge] {Name}: cannot connect to {_host}:{_port} — {e.Message}");
            CloseConnectionLocked();
            return false;
        }
    }

    void CloseConnectionLocked()
    {
        try { _stream?.Dispose(); } catch { }
        try { _client?.Close(); } catch { }

        _stream = null;
        _client = null;
        _readBuffer.Length = 0;
    }

    void WriteLineLocked(string message)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message + "\n");
        _stream.Write(bytes, 0, bytes.Length);
        _stream.Flush();
    }

    string ReadLineLocked()
    {
        while (true)
        {
            int newline = IndexOfNewline(_readBuffer);

            if (newline >= 0)
            {
                string line = _readBuffer.ToString(0, newline);
                _readBuffer.Remove(0, newline + 1);
                return line;
            }

            int read = _stream.Read(_chunk, 0, _chunk.Length);

            if (read <= 0)
            {
                return null;
            }

            _readBuffer.Append(Encoding.UTF8.GetString(_chunk, 0, read));
        }
    }

    static int IndexOfNewline(StringBuilder sb)
    {
        for (int i = 0; i < sb.Length; i++)
        {
            if (sb[i] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    #endregion
}
