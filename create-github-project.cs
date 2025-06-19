using Microsoft.Azure.Functions.Worker;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
using Microsoft.Azure.Functions.Worker.Http;
using System.Text.RegularExpressions;

namespace ShaneOdell.Functions;

public static class ProcessDesignDoc
{
	[Function("ProcessDesignDoc")]
	public static async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req, FunctionContext executionContext)
	{
		var body = await new StreamReader(req.Body).ReadToEndAsync();
		var design = JsonSerializer.Deserialize<DesignRequest>(body) ?? throw new InvalidDataException("Request body cannot be null");

		if (string.IsNullOrWhiteSpace(design.TranscriptText))
		{
			throw new InvalidDataException("TranscriptText cannot be null or empty.");
		}

		var designOutput = await ChatGptAgent.GenerateFromDesignAsync(design.TranscriptText);

		await GitHubClient.CreateRepoWithIssuesAsync(designOutput);

		var response = req.CreateResponse(HttpStatusCode.OK);

		await response.WriteStringAsync("Repo and issues created.");

		return response;
	}
}

public class DesignRequest
{
	public string? TranscriptText { get; set; }
}

public static class ChatGptAgent
{
	public static async Task<DesignOutput> GenerateFromDesignAsync(string transcript)
	{
		var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
		var prompt = $"You are an expert software engineer. From the transcript below, generate: - A project name - A README.md - A file structure - A list of GitHub issues to complete the project Transcript: \"{transcript}\"";

		var payload = new
		{
			model = "gpt-4",
			messages = new[]
			{
				new { role = "system", content = "You generate software project specs from transcripts." },
				new { role = "user", content = prompt }
			}
		};

		var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
		{
			Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
		};

		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

		var response = await new HttpClient().SendAsync(request);
		var json = await response.Content.ReadAsStringAsync();

		var parsed = JsonDocument.Parse(json);
		var content = parsed.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? throw new InvalidOperationException("No content returned from OpenAI API");

		return DesignOutputParser.Parse(content);
	}
}

public static class GitHubClient
{
	public static async Task CreateRepoWithIssuesAsync(DesignOutput output)
	{
		var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
		var httpClient = new HttpClient();

		httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
		httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("design-bot");

		// Create repository
		var repoBody = new
		{
			name = output.ProjectName,
			@private = true,
			auto_init = true,
			description = "Auto-generated project from meeting transcript"
		};

		await httpClient.PostAsync("https://api.github.com/user/repos", new StringContent(JsonSerializer.Serialize(repoBody), Encoding.UTF8, "application/json"));

		// Create README.md
		var encodedContent = Convert.ToBase64String(Encoding.UTF8.GetBytes(output.Readme));

		var createFileBody = new
		{
			message = "Initial commit",
			content = encodedContent
		};
		
		await httpClient.PutAsync($"https://api.github.com/repos/odellsha/{output.ProjectName}/contents/README.md", new StringContent(JsonSerializer.Serialize(createFileBody), Encoding.UTF8, "application/json"));

		// Create issues
		foreach (var issue in output.Issues)
		{
			var issueBody = new { title = issue };
			await httpClient.PostAsync($"https://api.github.com/repos/odellsha/{output.ProjectName}/issues", new StringContent(JsonSerializer.Serialize(issueBody), Encoding.UTF8, "application/json"));
		}
	}
}

public class DesignOutput
{
	public string ProjectName { get; set; } = string.Empty;

	public string Readme { get; set; } = string.Empty;

	public List<string> Issues { get; set; } = [];
}

public static partial class DesignOutputParser
{
	public static DesignOutput Parse(string content)
	{
		var output = new DesignOutput();

		// Extract Project Name
		var nameMatch = ProjectNameRegex().Match(content);
		output.ProjectName = nameMatch.Success ? nameMatch.Groups[1].Value.Trim().Replace(" ", "-").ToLowerInvariant() : "auto-project";

		// Extract README
		var readmeMatch = ReadmeRegex().Match(content);
		output.Readme = readmeMatch.Success ? readmeMatch.Groups[1].Value.Trim() : "# Auto-generated Project\n\nGenerated from transcript.";

		// Extract Issues
		var issues = new List<string>();
		var issuesMatch = IssuesRegex().Match(content);

		if (issuesMatch.Success)
		{
			var issuesText = issuesMatch.Groups[1].Value.Trim();
			foreach (var line in issuesText.Split('\n'))
			{
				var trimmed = line.Trim().TrimStart('-', '*').Trim();
				if (!string.IsNullOrWhiteSpace(trimmed))
					issues.Add(trimmed);
			}
		}

		output.Issues = issues;
		return output;
	}

	[GeneratedRegex(@"Project Name[:\s]*([^\r\n]+)", RegexOptions.IgnoreCase, "en-US")]
	private static partial Regex ProjectNameRegex();

	[GeneratedRegex(@"README(?:\.md)?[:\s]*([\s\S]+?)Issues[:\s]*", RegexOptions.IgnoreCase, "en-US")]
	private static partial Regex ReadmeRegex();

	[GeneratedRegex(@"Issues[:\s]*([\s\S]+)$", RegexOptions.IgnoreCase, "en-US")]
	private static partial Regex IssuesRegex();
}
