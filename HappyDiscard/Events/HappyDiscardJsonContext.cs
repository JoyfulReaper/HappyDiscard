/*
 * Happy Discard Service
 * Copyright (c) 2026 Kyle Givler
 * Licensed under the MIT License.
 */


using System.Text.Json;
using System.Text.Json.Serialization;

namespace HappyDiscard.Events;


[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(DiscardServiceStartedEvent))]
[JsonSerializable(typeof(DiscardStoppedEvent))]
[JsonSerializable(typeof(DiscardStartedEvent))]
internal sealed partial class HappyDiscardJsonContext : JsonSerializerContext;