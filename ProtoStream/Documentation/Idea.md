# ProtoStream

A system in witch the User Describes a Protocol to be used.

## User-Concern

- Providing the Description
- Providing the Stream
- Threading

## What should ProtoStream Support?

- Async requests via the same Stream
- Sync requests (Ping-Pong) via the same Stream
- Protocol Switching

## Components

### Message

The Message that gets transported via the Stream.

#### Decoding and Encoding

Each Message must be Encoded or Decoded, depending on the direction of travel. This should happen via Standardized Interfaces.

- `IMessageEncoder` Encodes any given Message to a Stream sendable Format.
- `IMessageDecoder` Decodes any given Message from a Stream receivable Format.

An decoder and an Encoder can declare Message level Errors.

### Protocol-Instance State

The State that describes the Protocol Instance. This should be a Summary with all relevant Data in it.

#### Instance State Reducer

A Mechanism to Reduce all incoming Messages into a unified State (for each Protocol-Instance).

- `IProtocolInstanceReducer` Reduces the Message into a State-Object.

### Protocol-Instance Action

This is used to progress in the Protocol to different Areas of the Protocol.

#### Action Dispatcher

This is the mechanism that takes the current Message, the current Protocol-Instance-State and returns an Action.
It has also the opportunity to declare Failure.

#### Action Kinds

An Action must have a defined behavior. This behavior defined what the next step is (Wait for message, Send a message or Delegate to User).

Kinds:
- `WaitKind` It should wait for another Message.
- `SendMessageKind` It must know want Message to send and just Sends it.
- `DelegateToUserKind` The User resolves a new Action (either Wait or SendMessage).

### Protocol Switching

This must be a declared feature of the Protocol you want to switch to. This includes a reducer to get a Valid new-protocol-state. 
The switch is normally a response to an Action.

- `IProtocolSwitchReducer<FromProtocol, ToProtocol>` has right definitions how to switch - how must the state look like.

The ProtocolSwitchReducer can declare Protocol level Errors.

## Flow

```
[Stream], [State = InitState] -> 
:LOOP
<Decoding>(Stream):[Message] -> 
<Reducing>(State, Message):[State] -> 
<Dispatching>(State, Message):[NextAction] -> 
{NextAction}
| NotOurTodo -> :LOOP:
| OurTodo -> 
	<GenNewMessage>(State):[NextMessage] ->
	<Encoding>(NextMessage) -> [Stream]
```

## Usage

```cs
public readonly static ProtocolDescription HttpProtocol = ...;

public async Task RunServer()
{
	Stream tcpStream = //Get Data Stream;

	var instance = HttpProtocol.GetInstance(tcpStream);


}
```