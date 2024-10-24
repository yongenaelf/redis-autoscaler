using System.Diagnostics;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();

var consumerGroupName = Environment.GetEnvironmentVariable("CONSUMER_GROUP_NAME") ?? "consumergroup";
var redisConnectionString = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING") ?? "localhost";
var buildStreamName = Environment.GetEnvironmentVariable("BUILD_STREAM_NAME") ?? "buildstream";
var buildResultStreamName = Environment.GetEnvironmentVariable("BUILD_RESULT_STREAM_NAME") ?? "buildresultstream";
var testStreamName = Environment.GetEnvironmentVariable("TEST_STREAM_NAME") ?? "teststream";
var testResultStreamName = Environment.GetEnvironmentVariable("TEST_RESULT_STREAM_NAME") ?? "testresultstream";
var timeout = int.Parse(Environment.GetEnvironmentVariable("TIMEOUT") ?? "10000");

var redis = ConnectionMultiplexer.Connect(redisConnectionString);
IDatabase db = redis.GetDatabase();

var allResults = new Dictionary<string, string>();

// post formdata file upload to Redis stream
app.MapPost("/build", async (IFormFile file) =>
{
    var result = await SendToRedisAndWatchForResults(buildStreamName, file, buildResultStreamName);
    return Results.Ok(result);
})
.DisableAntiforgery();

app.MapPost("/test", async (IFormFile file) =>
{
    var result = await SendToRedisAndWatchForResults(testStreamName, file, testResultStreamName);
    return Results.Ok(result);
})
.DisableAntiforgery();

app.Run();

// reusable code for the above two endpoints
async Task<string> SendToRedisAndWatchForResults(string streamName, IFormFile file, string resultStreamName)
{
    await Init();

    string key = Guid.NewGuid().ToString();

    // convert file to byte array
    byte[] fileBytes;
    using (var ms = new MemoryStream())
    {
        file.CopyTo(ms);
        fileBytes = ms.ToArray();
    }

    // convert file to base64 string
    var fileBase64 = Convert.ToBase64String(fileBytes);

    // Adding a message to the stream
    // var messageId = await db.StreamAddAsync(streamName, [new("id", key), new("file", fileBase64)], "*", 100);

    // https://github.com/StackExchange/StackExchange.Redis/issues/1718#issuecomment-1219592426
    var Expiry = TimeSpan.FromMinutes(3);
    var add = await db.ExecuteAsync("XADD", streamName, "MINID", "=", DateTimeOffset.UtcNow.Subtract(Expiry).ToUnixTimeMilliseconds(), "*", "id", key, "file", fileBase64);
    var msgId = add.ToString();
    Console.WriteLine($"Message added to stream {streamName} with id {key} as {msgId}");

    var timer = Stopwatch.StartNew();

    var consumer = Guid.NewGuid().ToString();

    while (timer.ElapsedMilliseconds < timeout)
    {
        var result = await db.StreamReadGroupAsync(resultStreamName, consumerGroupName, consumer, ">", 1);
        if (result.Any())
        {
            var current = result.First();
            var dict = ParseResult(current);

            await db.StreamAcknowledgeAsync(resultStreamName, consumerGroupName, current.Id);

            try
            {
                allResults[key] = dict["message"];
            }
            catch (Exception)
            {
                try
                {
                    allResults[key] = dict["error"];
                }
                catch (Exception ex)
                {
                    allResults[key] = ex.Message;
                }
            }

            // if the message we are looking for is processed, return the result
            if (allResults.TryGetValue(key, out string? value))
            {
                Console.WriteLine($"Message with id {key} processed in {timer.ElapsedMilliseconds}ms");
                return value;
            }
        }
        await Task.Delay(1000);
    }

    // since there is a timeout, acknowledge the original message to avoid reprocessing
    // await db.StreamAcknowledgeAsync(streamName, consumerGroupName, messageId);

    return "Timeout";
}

Dictionary<string, string> ParseResult(StreamEntry entry) => entry.Values.ToDictionary(x => x.Name.ToString(), x => x.Value.ToString());

async Task Init()
{
    await InitStream(buildStreamName, consumerGroupName);
    await InitStream(buildResultStreamName, consumerGroupName);
    await InitStream(testStreamName, consumerGroupName);
    await InitStream(testResultStreamName, consumerGroupName);
}

async Task InitStream(string streamName, string groupName)
{
    if (!await db.KeyExistsAsync(streamName) ||
    (await db.StreamGroupInfoAsync(streamName)).All(x => x.Name != groupName))
    {
        await db.StreamCreateConsumerGroupAsync(streamName, groupName, "0-0", true);
    }
}