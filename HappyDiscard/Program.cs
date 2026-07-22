/*
 * Happy Discard Server
 * Copyright (c) 2026 Kyle Givler
 * Licensed under the MIT License.
 */

using HappyDiscard;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<DiscardWorker>();

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "Happy Discard Service";
});

var discardSection = builder.Configuration.GetSection(HappyDiscardOptions.SectionName);
builder.Services
    .AddOptions<HappyDiscardOptions>()
    .Bind(discardSection)
    .Validate(options => options.Port is > 0 and <= 65535, "Discard:Port must be between 1 and 65535.")
    .Validate(options => options.MaxConcurrentConnections > 0, "Discard:MaxConcurrentConnections must be positive.")
    .Validate(options => options.RequestTimeoutSeconds > 0, "Discard:RequestTimeoutSeconds must be positive.")
    .ValidateOnStart();

var host = builder.Build();
host.Run();
