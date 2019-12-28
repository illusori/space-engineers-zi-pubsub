# space-engineers-zi-pubsub
Space Engineers - Zephyr Industries PubSub Controller

## Living in the Future(tm)

*Ever wished that scripts could easily talk to one another and share data? Have you dreamed of a paradise of interoperability and lightly coupled plugins?*

Zephyr Industries has what you need, I'm here to tell you about their great new product: _Zephyr Industries PubSub Controller_.

Subscribe to consume events from event providers without caring if they exist or how they work! Send events to consumers without caring if they exist or how they work! You don't even need to know how many consumers of your data there are, or what script is providing your data, as long as they follow a consistent event format!

Life has never been so good, that's what Living in the Future(tm) means!

Small print: Zephyr Industries PubSub isn't aimed to directly provide any player-visible behaviour itself. If you install it on a block somewhere, any compatible scripts will be able to use it to enhance their functionality however.

## Instructions:
* Place on a Programmable Block.
* That's it, there's no user-configurable stuff to do. Any scripts that use it will find it.
* If you really want some debugging info: Mark LCD panels by adding a tag to their name and on the next base scan (every 30s or so) the script will start using it.
  * `@PubSubDebugDisplay` displays info useful for script development, including performance.
  * `@PubSubWarningDisplay` displays any issues the script encountered.
  * Additionally if you have [Zephyr Industries Bar Charts](https://github.com/illusori/space-engineers-zi-bar-charts) installed it will provide two charts `"PubSub Exec Time"` and `"PubSub Instr Load"` that you can display. See [Charts](#charts) for more.

## Instructions for Script Authors

### What is PubSub?

PubSub is a publish/subscriber event pattern where "providers" send events to a broker, and the broker then passes those events on to "consumers". Publishers don't need to know who is subscribed, and subscribers don't need to know who is publishing.

Some example benefits:

I write a script that publishes an event stream of base statistics data. I also write a script that consumes an event stream of statistics data and displays it as bar charts on LCD panels.

So far PubSub doesn't gain me much, other than letting me split that into two scripts.

However, unknown to me, another script author writes a better bar chart display script. They then make it consume the same event stream and users can just use their bar chart script instead of mine, and it it Just Works. My script that publishes the events doesn't know anything about the new script, but it still works.

Similarly, someone could write another script that outputs different statistics of some kind, sends it to the same statistics stream and my bar chart library can display it.

There's more: all these scripts could be running at once. PubSub makes sure everything speaks to everything else the way it needs to. They just need to adopt the same event format.

### So, how does it work?

To send events, you just send a command to the programmable block running the PubSub script.

To receive events, you first have to subscribe with the PubSub programmable block to receive those events. To do this, you just need to issue a `pubsub.subscribe` event with the `EntityId` of the programmable block your script is running on. Events will then be sent to the consuming script as commands. Simple!

(Don't worry about `EntityId` being mutable, it's only used temporarily during registration.)

### OK, how does that work in code?

#### Basic Setup

Whether you're consuming or producing events you'll need some basic code to find and talk to the PubSub controller:

```C#
// Name used to find the PubSub Controller.
const string PUBSUB_SCRIPT_NAME = "Zephyr Industries PubSub Controller";
// An ID used to indicate what script is sending the data, useful for debugging!
// This should be a string with no space, ideally shortish, and using a dot.notation
// name space for your scripts is good practice in case any filtering needs to be done.
const string PUBSUB_ID = "zi.inv-display";

// Store this somewhere global so you don't look it up each time.
List<IMyProgrammableBlock> _pubsub_blocks = new List<IMyProgrammableBlock>();

int _cycles = 0;

public Program() {
    FindPubSubBlocks();
    Runtime.UpdateFrequency |= UpdateFrequency.Update100;
}

public Main(string argument, UpdateType updateSource) {
    _cycles++;
    if ((updateSource & UpdateType.Update100) != 0) {
        if (_cycles % 30) {
            // Every 30 updates of Update100, refresh the list of PubSub blocks.
            FindPubSubBlocks();
        }
    }
}

// This finds and caches the PubSub blocks on a grid. Find a balance between running it often
// enough to detect changes and slowly enough not to waste CPU. I do it every 30 seconds myself.
public void FindPubSubBlocks() {
    _pubsub_blocks.Clear();
    GridTerminalSystem.GetBlocksOfType<IMyProgrammableBlock>(_pubsub_blocks, block => block.CustomName.Contains(PUBSUB_SCRIPT_NAME));
}

// This publishes an event to all the PubSub controllers we know about.
// You proably only will have one of them, but docking and merging grids might change that, so send to them all.
public void PublishEvent(string event_name, string event_args) {
    foreach (IMyProgrammableBlock block in _pubsub_blocks) {
        if (block != null) {
            block.TryRun($"event {PUBSUB_ID} {event_name} {event_args}");
        }
    }
}
```

This gets you _able_ to talk to the PubSub controller, but it doesn't actually do any talking. For that there's two different approaches: one if your're a consumer and one if you're a provider. And you do both ways if you're both a consumer and a provider.

#### Provider Setup

Providing events is easy, so let's do that first. Following on from the setup above:

```C#
// Call this from Main() or somewhere. I don't care. :D
public void MyCodeThatDoesSomeStuff() {
    // You've got value from some calculation.
    double wind_turbine_efficiency = SomeCalculationOrOther();

    // Let's issue it as a datapoint.issue event.

    // Event names should be dot.notation to namespace them.
    // You should probably try to be descriptive and general purpose so other people can
    // use your event format, rather than try to make it specific to your scripts.

    // This event takes a dataset name and a datapoint value as arguments.
    PublishEvent("datapoint.issue", $"\"wind_turbine_efficiency\" {wind_turbine_efficiency}");
}
```

The format of the argument for events is whatever agreed convention there is between the providers of those events and the consumers. PubSub just takes a string and passes it on without looking at it.

#### Consumer Setup

Consuming events is a little more complicated. First we need to subscribe to receive events. To do that, let's modify the basic setup a little:

```C#
public void FindPubSubBlocks() {
    _pubsub_blocks.Clear();
    GridTerminalSystem.GetBlocksOfType<IMyProgrammableBlock>(_pubsub_blocks, block => block.CustomName.Contains(PUBSUB_SCRIPT_NAME) && block.IsSameConstructAs(Me));

    // Every time we scan for pubsub blocks, (re)subscribe for the events we want to consume.
    // Doing it each time is a little wasteful, but it means we automatically correct for when new
    // PubSub controllers are found, or for if they've had their script stopped and restarted.

    // Suscribe for the datapoint.issue event, the entityid lets the PubSub controller know who we are.
    PublishEvent("pubsub.subscribe", $"datapoint.issue {Me.EntityId}");
    // Nothing stops you from subscribing to as many event types as you want to consume.
    PublishEvent("pubsub.subscribe", $"dataset.create {Me.EntityId}");
}
```

Registration is done, now we need to handle event receiving. Events are sent as an argument to your script... it takes the exact same format as the command sent to the PubSub script, so feel free to write your own parsing, but here's a basic example to get you started:

```C#
public void Main(string argument, UpdateType updateSource) {
    // ... all the rest of the Main() stuff you usually do ...
    if (argument != "") {
        // Argument is something like: `event zi.inv-display datapoint.issue "Max Stored Power" 6.7`
	_arguments = new List<string>(argument.Split(' '));
        if (_arguments.Count > 0 && _arguments[0] == "event") {
            if (_arguments[2] == "datapoint.issue") {
                // I'll leave how you parse quoted-strings and other messiness in the rest of the arguments up to you. :D
            } else if (_arguments[2] == "dataset.create") {
            }
        }
    }
}
```

## Issues

Multiple PubSub Controllers will end up sending multiple events to consumers. Consumers should probably try to handle this gracefully. This is somewhat unavoidable with grid merging being a thing.

If you stick to docking your grids with connectors then the snippet for finding a PubSub controller will only find PubSub controllers on the same physical grid. If you dock using rotors or merge blocks then you'll have to figure out a way to manage that complexity yourself I'm afraid.

Once a block has subscribed to a PubSub controller as a listener, that PubSub controller will be able to send it events even if they end up on separate grids, until the PubSub controller loses the reference when it restarts (on game reload for example). This behaviour should be considered an unintended side-effect and may well change in future versions.

## Charts

Zephyr Industries PubSub Controller integrates with [Zephyr Industries Bar Charts](https://github.com/illusori/space-engineers-zi-bar-charts) and provides the following charts:

Series name | Default Unit | Description
:---: | :---: | :---
PubSub Exec Time | us | (debug) Microsecond timings of how long the script ran for on each invocation.
PubSub Instr Load | - | (debug) Instruction count complexity load for each invocation of the script.
PubSub Events Rx | - | (debug) Number of events received by this controller this cycle.
PubSub Events Tx | - | (debug) Number of events transmitted by this controller this cycle.

## Contributing:

Zephyr Industries PubSub Controller is open source, under an MIT license. You can contribute to or copy the code at https://github.com/illusori/space-engineers-zi-pubsub.
