string _script_name = "Zephyr Industries PubSub Controller";
string _script_version = "1.0.1";

string _script_title = null;
string _script_title_nl = null;

const string PUBSUB_ID = "zi.pubsub";

const int PANELS_DEBUG = 0;
const int PANELS_WARN  = 1;
const int SIZE_PANELS  = 2;

const string CHART_TIME = "PubSub Exec Time";
const string CHART_LOAD = "PubSub Instr Load";
const string CHART_EVENTS_RX = "PubSub Events Rx";
const string CHART_EVENTS_TX = "PubSub Events Tx";

List<string> _panel_tags = new List<string>(SIZE_PANELS) { "@PubSubDebugDisplay", "@PubSubWarningDisplay" };

/* Genuine global state */
int _cycles = 0;

// event_name => list of subscriber programmable blocks
Dictionary<string, HashSet<IMyProgrammableBlock>> _subscriptions = new Dictionary<string, HashSet<IMyProgrammableBlock>>();

List<List<IMyTextPanel>> _panels = new List<List<IMyTextPanel>>(SIZE_PANELS);
List<string> _panel_text = new List<string>(SIZE_PANELS) { "", "", };

double _time_total = 0.0, _last_run_time_ms_tally = 0.0;
int _events_rx = 0, _events_tx = 0;

/* Reused single-run state objects, only global to avoid realloc/gc-thrashing */
// FIXME: _chart here? _panel?
MyCommandLine _command_line = new MyCommandLine();
HashSet<IMyProgrammableBlock> _subscribers;

public Program() {
    _script_title = $"{_script_name} v{_script_version}";
    _script_title_nl = $"{_script_name} v{_script_version}\n";

    for (int i = 0; i < SIZE_PANELS; i++) {
        _panels.Add(new List<IMyTextPanel>());
    }

    FindPanels();

    if (!Me.CustomName.Contains(_script_name)) {
        // Update our block to include our script name
        Me.CustomName = $"{Me.CustomName} - {_script_name}";
    }
    Log(_script_title);

    // Only for chart updates
    Runtime.UpdateFrequency |= UpdateFrequency.Update100;
}

public void Save() {
}

public void Main(string argument, UpdateType updateSource) {
    try {
        // Tally up all invocation times and record them as one on the non-command runs.
        _last_run_time_ms_tally += Runtime.LastRunTimeMs;
        if ((updateSource & UpdateType.Update100) != 0) {
	    _cycles++;

	    ProcessCommand($"event {PUBSUB_ID} datapoint.issue \"{CHART_TIME}\" {TimeAsUsec(_last_run_time_ms_tally)}");
            if (_cycles > 1) {
                _time_total += _last_run_time_ms_tally;
                if (_cycles == 201) {
                    Warning($"Total time after 200 cycles: {_time_total}ms.");
                }
            }
            _last_run_time_ms_tally = 0.0;

            ClearPanels(PANELS_DEBUG);

            Log(_script_title_nl);

            if ((_cycles % 30) == 0) {
                FindPanels();
    	        ProcessCommand($"event {PUBSUB_ID} dataset.create \"{CHART_TIME}\" \"us\"");
    	        ProcessCommand($"event {PUBSUB_ID} dataset.create \"{CHART_LOAD}\" \"%\"");
    	        ProcessCommand($"event {PUBSUB_ID} dataset.create \"{CHART_EVENTS_RX}\" \"\"");
    	        ProcessCommand($"event {PUBSUB_ID} dataset.create \"{CHART_EVENTS_TX}\" \"\"");
            }

	    double load = (double)Runtime.CurrentInstructionCount * 100.0 / (double)Runtime.MaxInstructionCount;
	    ProcessCommand($"event {PUBSUB_ID} datapoint.issue \"{CHART_LOAD}\" {load}");

            // Slightly sneaky, push the counts for sending rx/tx themseles onto the next cycle.
            int rx = _events_rx, tx = _events_tx;
            _events_rx = 0;
            _events_tx = 0;
	    ProcessCommand($"event {PUBSUB_ID} datapoint.issue \"{CHART_EVENTS_RX}\" {rx}");
	    ProcessCommand($"event {PUBSUB_ID} datapoint.issue \"{CHART_EVENTS_TX}\" {tx}");

	    //long load_avg = (long)Chart.Find(CHART_LOAD).Avg;
	    //long time_avg = (long)Chart.Find(CHART_TIME).Avg;
	    //Log($"  [Avg ] Load {load_avg}% in {time_avg}us");

            // Start at T-1 - exec time hasn't been updated yet.
            //for (int i = 1; i < 16; i++) {
                /* FIXME:
                long load = (long)Chart.Find(CHART_LOAD).Datapoint(-i);
                long time = (long)Chart.Find(CHART_TIME).Datapoint(-i);
                Log($"  [T-{i,-2}] Load {load}% in {time}us");
                 */
            //}
            // FIXME: events/subscribers Log($"Charts: {Chart.Count}, DrawBuffers: {Chart.BufferCount}");
            Log($"[Cycle {_cycles}]\n  Events received: {rx}, Events transmitted: {tx}");
            FlushToPanels(PANELS_DEBUG);
        }
        //if ((updateSource & (UpdateType.Trigger | UpdateType.Terminal)) != 0) {
        if (argument != null) {
            ProcessCommand(argument);
	}
    } catch (Exception e) {
        string mess = $"An exception occurred during script execution.\nException: {e}\n---";
        Log(mess);
        Warning(mess);
        FlushToPanels(PANELS_DEBUG);
        throw;
    }
}

public void ProcessCommand(string argument) {
    if (_command_line.TryParse(argument)) {
	string command = _command_line.Argument(0);
	if (command == null) {
	    Log("No command specified");
	} else if (command == "event") {
	    ProcessEvent(argument);
	} else {
	    Log($"Unknown command {command}");
	}
    }
}

// Format: event <source> <event> <event data...>
// eg: event zi.bar-charts pubsub.register datapoint.issue <entity_id>
//     event zi.inv-display datapoint.issue "Max Stored Power" 6.7
public void ProcessEvent(string argument) {
    string source     = _command_line.Argument(1);
    string event_name = _command_line.Argument(2);
    //Warning($"Received event '{event_name}' from source '{source}'.");

    _events_rx++;
    if (_subscriptions.TryGetValue(event_name, out _subscribers)) {
        foreach (IMyProgrammableBlock block in _subscribers) {
            //Warning($"Sending event '{event_name}' to '{block.CustomName}'.");
            block.TryRun(argument);
            _events_tx++;
        }
    }

    if (event_name == "pubsub.register") {
        // eg: event zi.bar-charts pubsub.register datapoint.issue <entity_id>
        string subscription = _command_line.Argument(3);
        long entity_id = long.Parse(_command_line.Argument(4), System.Globalization.CultureInfo.InvariantCulture);

        // register new listener
        IMyProgrammableBlock block = (IMyProgrammableBlock)GridTerminalSystem.GetBlockWithId(entity_id);
        if (block != null) {
            AddSubscriber(subscription, block);
        }
    } else if (event_name == "pubsub.unregister") {
        // eg: event zi.bar-charts pubsub.unregister datapoint.issue <entity_id>
        string subscription = _command_line.Argument(3);
        long entity_id = long.Parse(_command_line.Argument(4), System.Globalization.CultureInfo.InvariantCulture);

        // unregister listener
        IMyProgrammableBlock block = (IMyProgrammableBlock)GridTerminalSystem.GetBlockWithId(entity_id);
        if (block != null) {
            RemoveSubscriber(subscription, block);
        }
    }
}

public void AddSubscriber(string event_name, IMyProgrammableBlock subscriber) {
    HashSet<IMyProgrammableBlock> subscribers;

    if (_subscriptions.TryGetValue(event_name, out subscribers)) {
        subscribers.Add(subscriber);
    } else {
        subscribers = new HashSet<IMyProgrammableBlock>() { subscriber };
        _subscriptions.Add(event_name, subscribers);
    }
}

public void RemoveSubscriber(string event_name, IMyProgrammableBlock subscriber) {
    HashSet<IMyProgrammableBlock> subscribers;

    if (_subscriptions.TryGetValue(event_name, out subscribers)) {
        subscribers.Remove(subscriber);
    }
}

public double TimeAsUsec(double t) {
    //return (t * 1000.) / TimeSpan.TicksPerMillisecond;
    return t * 1000.0;
}

public void FindPanels() {
    for (int i = 0; i < SIZE_PANELS; i++) {
        _panels[i].Clear();
        GridTerminalSystem.GetBlocksOfType<IMyTextPanel>(_panels[i], block => block.CustomName.Contains(_panel_tags[i]) && block.IsSameConstructAs(Me));
        for (int j = 0, szj = _panels[i].Count; j < szj; j++) {
            _panels[i][j].ContentType = ContentType.TEXT_AND_IMAGE;
            _panels[i][j].Font = "Monospace";
            _panels[i][j].FontSize = 0.5F;
            _panels[i][j].TextPadding = 0.5F;
            _panels[i][j].Alignment = TextAlignment.LEFT;
        }
    }
}

public void ClearAllPanels() {
    for (int i = 0; i < SIZE_PANELS; i++) {
        ClearPanels(i);
    }
}

public void ClearPanels(int kind) {
    _panel_text[kind] = "";
}

public void WritePanels(int kind, string s) {
    _panel_text[kind] += s;
}

public void PrependPanels(int kind, string s) {
    _panel_text[kind] = s + _panel_text[kind];
}

public void FlushToAllPanels() {
    for (int i = 0; i < SIZE_PANELS; i++) {
        FlushToPanels(i);
    }
}

public void FlushToPanels(int kind) {
    for (int i = 0, sz = _panels[kind].Count; i < sz; i++) {
        if (_panels[kind][i] != null) {
            _panels[kind][i].WriteText(_panel_text[kind], false);
        }
    }
}

public void Log(string s) {
    WritePanels(PANELS_DEBUG, s + "\n");
    Echo(s);
}

public void Warning(string s) {
    // Never clear buffer and and always immediately flush.
    // Prepend because long text will have the bottom hidden.
    PrependPanels(PANELS_WARN, $"[{DateTime.Now,11:HH:mm:ss.ff}] {s}\n");
    FlushToPanels(PANELS_WARN);
}
