string _script_name = "Zephyr Industries PubSub Controller";
string _script_version = "1.0.0";

string _script_title = null;
string _script_title_nl = null;

const string PUBSUB_ID = "zi.pubsub";

const int PANELS_DEBUG = 0;
const int PANELS_WARN  = 1;
const int SIZE_PANELS  = 2;

const string CHART_TIME = "PubSub Exec Time";
const string CHART_LOAD = "PubSub Instr Load";

List<string> _panel_tags = new List<string>(SIZE_PANELS) { "@PubSubDebugDisplay", "@PubSubWarningDisplay" };

/* Genuine global state */
int _cycles = 0;

// event_name => list of subscriber programmable blocks
Dictionary<string, HashSet<IMyProgrammableBlock>> _subscriptions = new Dictionary<string, HashSet<IMyProgrammableBlock>>();

List<List<IMyTextPanel>> _panels = new List<List<IMyTextPanel>>(SIZE_PANELS);
List<string> _panel_text = new List<string>(SIZE_PANELS) { "", "", };

double time_total = 0.0;
double last_run_time_ms_tally = 0.0;

/* Reused single-run state objects, only global to avoid realloc/gc-thrashing */
// FIXME: _chart here? _panel?
List<string> _arguments = new List<string>();

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
        last_run_time_ms_tally += Runtime.LastRunTimeMs;
        if ((updateSource & UpdateType.Update100) != 0) {
	    _cycles++;

	    ProcessArgument($"event {PUBSUB_ID} datapoint.issue \"{CHART_TIME}\" {TimeAsUsec(last_run_time_ms_tally)}");
            if (_cycles > 1) {
                time_total += last_run_time_ms_tally;
                if (_cycles == 201) {
                    Warning($"Total time after 200 cycles: {time_total}ms.");
                }
            }
            last_run_time_ms_tally = 0.0;

            ClearPanels(PANELS_DEBUG);

            Log(_script_title_nl);

            if ((_cycles % 30) == 0) {
                FindPanels();
    	        ProcessArgument($"event {PUBSUB_ID} dataset.create \"{CHART_TIME}\" \"us\"");
    	        ProcessArgument($"event {PUBSUB_ID} dataset.create \"{CHART_LOAD}\" \"%\"");
            }

	    double load = (double)Runtime.CurrentInstructionCount * 100.0 / (double)Runtime.MaxInstructionCount;
	    ProcessArgument($"event {PUBSUB_ID} datapoint.issue \"{CHART_LOAD}\" {load}");

	    //long load_avg = (long)Chart.Find(CHART_LOAD).Avg;
	    //long time_avg = (long)Chart.Find(CHART_TIME).Avg;
	    //Log($"Load avg {load_avg}% in {time_avg}us");

            // Start at T-1 - exec time hasn't been updated yet.
            //for (int i = 1; i < 16; i++) {
                /* FIXME:
                long load = (long)Chart.Find(CHART_LOAD).Datapoint(-i);
                long time = (long)Chart.Find(CHART_TIME).Datapoint(-i);
                Log($"  [T-{i,-2}] Load {load}% in {time}us");
                 */
            //}
            // FIXME: events/subscribers Log($"Charts: {Chart.Count}, DrawBuffers: {Chart.BufferCount}");
            FlushToPanels(PANELS_DEBUG);
        }
        //if ((updateSource & (UpdateType.Trigger | UpdateType.Terminal)) != 0) {
        if (argument != "") {
            ProcessArgument(argument);
        }
    } catch (Exception e) {
        string mess = $"An exception occurred during script execution.\nException: {e}\n---";
        Log(mess);
        Warning(mess);
        FlushToPanels(PANELS_DEBUG);
        throw;
    }
}

public void ProcessArgument(string argument) {
    //Warning($"Running command '{argument}'.");
    // Deliberately simplistic parsing for speed.
    int first_space = argument.IndexOf(" "),
	second_space = first_space == -1 ? -1 : argument.IndexOf(" ", first_space + 1),
	third_space = second_space == -1 ? -1 : argument.IndexOf(" ", second_space + 1);
    if (first_space == -1 || second_space == -1 || third_space == -1) {
        Warning($"Couldn't parse argument '{argument}': less than three words.");
        return;
    }
    string command = argument.Substring(0, first_space);
    string source = argument.Substring(first_space + 1, second_space - first_space - 1);
    string event_name = argument.Substring(second_space + 1, third_space - second_space - 1);
    string rest = argument.Substring(third_space + 1, argument.Length - third_space - 1);

    //Warning($"command '{command}' source '{source}' event '{event_name}'\n  rest '{rest}'.");

    if (command == "event") {
        ProcessEvent(argument, source, event_name, rest);
    }
}

// Format: event <source> <event> <event data...>
// eg: event zi.bar-charts pubsub.register datapoint.issue <entity_id>
//     event zi.inv-display datapoint.issue "Max Stored Power" 6.7
public void ProcessEvent(string argument, string source, string event_name, string rest) {
    //Warning($"Received event '{event_name}' from source '{source}'.");

    HashSet<IMyProgrammableBlock> subscribers;
    if (_subscriptions.TryGetValue(event_name, out subscribers)) {
        foreach (IMyProgrammableBlock block in subscribers) {
            //Warning($"Sending event '{event_name}' to '{block.CustomName}'.");
            block.TryRun(argument);
        }
    }

    if (event_name == "pubsub.register") {
        // eg: event zi.bar-charts pubsub.register datapoint.issue <entity_id>
        string[] args = rest.Split(' ');
        long entity_id = long.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
        string subscription = args[0];

        // register new listener
        IMyProgrammableBlock block = (IMyProgrammableBlock)GridTerminalSystem.GetBlockWithId(entity_id);
        if (block != null) {
            AddSubscriber(subscription, block);
        }
    } else if (event_name == "pubsub.unregister") {
        // eg: event zi.bar-charts pubsub.unregister datapoint.issue <entity_id>
        string[] args = rest.Split(' ');
        long entity_id = long.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
        string subscription = args[0];

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
        GridTerminalSystem.GetBlocksOfType<IMyTextPanel>(_panels[i], block => block.CustomName.Contains(_panel_tags[i]));
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
