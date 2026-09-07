// Security Policy Manager Tests
// Tests for SSRF protection, shell env sanitization, and redaction

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Splatter.Service.Security;
using Xunit;

namespace Splatter.Service.Tests.Security;

public class SecurityPolicyManagerTests
{
    #region SSRF Protection Tests

    [Theory]
    [InlineData("https://api.anthropic.com/v1/messages", true)]
    [InlineData("https://api.openai.com/v1/chat/completions", true)]
    [InlineData("http://localhost/admin", false)] // HTTP not HTTPS
    [InlineData("https://evil-site.com/steal-data", false)] // Not in allowlist
    [InlineData("https://metadata.google.internal/", false)] // Blocked cloud metadata
    public async Task ValidateUrlAsync_EnforcesPolicy(string url, bool expectedValid)
    {
        // Arrange
        var manager = CreateTestManager();

        // Act
        var isValid = await manager.ValidateUrlAsync(url, CancellationToken.None);

        // Assert
        Assert.Equal(expectedValid, isValid);
    }

    [Fact]
    public async Task ValidateUrlAsync_BlocksPrivateNetworks()
    {
        // Arrange
        var manager = CreateTestManager();
        var privateUrls = new[]
        {
            "https://192.168.1.1/admin",
            "https://10.0.0.1/internal",
            "https://172.16.0.1/secret"
        };

        // Act & Assert
        foreach (var url in privateUrls)
        {
            var isValid = await manager.ValidateUrlAsync(url, CancellationToken.None);
            Assert.False(isValid, $"Private network URL should be blocked: {url}");
        }
    }

    [Fact]
    public async Task ValidateUrlAsync_InvalidUrl_ReturnsFalse()
    {
        // Arrange
        var manager = CreateTestManager();

        // Act
        var isValid = await manager.ValidateUrlAsync("not-a-valid-url", CancellationToken.None);

        // Assert
        Assert.False(isValid);
    }

    #endregion

    #region Shell Environment Tests

    [Fact]
    public void SanitizeEnvironment_AllowsSafeVariables()
    {
        // Arrange
        var manager = CreateTestManager();
        var requestedEnv = new Dictionary<string, string>
        {
            ["PATH"] = "/usr/bin:/bin",
            ["HOME"] = "/home/user",
            ["LANG"] = "en_US.UTF-8"
        };

        // Act
        var sanitized = manager.SanitizeEnvironment(requestedEnv);

        // Assert
        Assert.Contains("PATH", sanitized.Keys);
        Assert.Contains("HOME", sanitized.Keys);
    }

    [Fact]
    public void SanitizeEnvironment_BlocksSensitiveVariables()
    {
        // Arrange
        var manager = CreateTestManager();
        var requestedEnv = new Dictionary<string, string>
        {
            ["PATH"] = "/usr/bin",
            ["ANTHROPIC_API_KEY"] = "sk-secret-key",
            ["AWS_SECRET_ACCESS_KEY"] = "aws-secret",
            ["DATABASE_URL"] = "postgres://secret"
        };

        // Act
        var sanitized = manager.SanitizeEnvironment(requestedEnv);

        // Assert
        Assert.DoesNotContain("ANTHROPIC_API_KEY", sanitized.Keys);
        Assert.DoesNotContain("AWS_SECRET_ACCESS_KEY", sanitized.Keys);
        Assert.DoesNotContain("DATABASE_URL", sanitized.Keys);
    }

    [Fact]
    public void SanitizeEnvironment_BlocksSensitivePatterns()
    {
        // Arrange
        var manager = CreateTestManager();
        var requestedEnv = new Dictionary<string, string>
        {
            ["MY_SECRET_KEY"] = "secret",
            ["APP_PASSWORD"] = "pass123",
            ["AUTH_TOKEN"] = "token123"
        };

        // Act
        var sanitized = manager.SanitizeEnvironment(requestedEnv);

        // Assert - patterns like *_KEY, *_PASSWORD, *_TOKEN should be blocked
        Assert.DoesNotContain("MY_SECRET_KEY", sanitized.Keys);
        Assert.DoesNotContain("APP_PASSWORD", sanitized.Keys);
        Assert.DoesNotContain("AUTH_TOKEN", sanitized.Keys);
    }

    #endregion

    #region Redaction Tests

    [Theory]
    [InlineData("My API key is sk-abcdefghijklmnopqrstuvwxyz", "[REDACTED]")]
    [InlineData("bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9abc", "bearer [REDACTED]")]
    [InlineData("password: mysecretpass123", "password: [REDACTED]")]
    public void Redact_RedactsSensitiveData(string input, string _expectedContains)
    {
        // Arrange
        var manager = CreateTestManager();

        // Act
        var redacted = manager.Redact(input);

        // Assert
        Assert.Contains("[REDACTED]", redacted);
        // Verify the sensitive part was redacted
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz", redacted);
    }

    [Fact]
    public void Redact_PreservesNormalText()
    {
        // Arrange
        var manager = CreateTestManager();
        var normalText = "This is a normal log message about the application status.";

        // Act
        var redacted = manager.Redact(normalText);

        // Assert
        Assert.Equal(normalText, redacted);
    }

    [Fact]
    public void Redact_HandlesEmptyInput()
    {
        // Arrange
        var manager = CreateTestManager();

        // Act & Assert
        Assert.Equal("", manager.Redact(""));
        Assert.Null(manager.Redact(null!));
    }

    #endregion

    #region Adversarial Assessment Tests

    [Theory]
    [InlineData("shell.execute", ThreatLevel.High)]
    [InlineData("file.delete", ThreatLevel.High)]
    [InlineData("file.write", ThreatLevel.Medium)]
    [InlineData("web.fetch", ThreatLevel.Medium)]
    [InlineData("credential.access", ThreatLevel.Critical)]
    [InlineData("scene.modify", ThreatLevel.Low)]
    public void AssessOperation_AssignsThreatLevel(string operation, ThreatLevel expectedLevel)
    {
        // Arrange
        var manager = CreateTestManager();

        // Act
        var assessment = manager.AssessOperation(operation, null);

        // Assert
        Assert.Equal(expectedLevel, assessment.ThreatLevel);
    }

    [Fact]
    public void AssessOperation_IdentifiesRisks()
    {
        // Arrange
        var manager = CreateTestManager();

        // Act
        var assessment = manager.AssessOperation("shell.execute", null);

        // Assert
        Assert.NotEmpty(assessment.Risks);
        Assert.NotEmpty(assessment.Mitigations);
        Assert.True(assessment.RequiresExplicitConsent);
    }

    #endregion

    #region Violation Tracking Tests

    [Fact]
    public void RecordViolation_TracksViolation()
    {
        // Arrange
        var manager = CreateTestManager();
        var violation = new SecurityViolation(
            "viol_1",
            SecurityViolationType.SsrfAttempt,
            "Attempted to access private network",
            ThreatLevel.High,
            "https://192.168.1.1",
            DateTimeOffset.UtcNow,
            null);

        // Act
        manager.RecordViolation(violation);
        var violations = manager.GetRecentViolations();

        // Assert
        Assert.Contains(violations, v => v.ViolationId == "viol_1");
    }

    [Fact]
    public void GetRecentViolations_LimitsResults()
    {
        // Arrange
        var manager = CreateTestManager();

        // Record many violations
        for (int i = 0; i < 150; i++)
        {
            manager.RecordViolation(new SecurityViolation(
                $"viol_{i}",
                SecurityViolationType.PolicyViolation,
                "Test violation",
                ThreatLevel.Low,
                null,
                DateTimeOffset.UtcNow,
                null));
        }

        // Act
        var violations = manager.GetRecentViolations(50);

        // Assert
        Assert.Equal(50, violations.Count);
    }

    #endregion

    #region Helper Methods

    private static SecurityPolicyManager CreateTestManager()
    {
        return new SecurityPolicyManager(NullLogger<SecurityPolicyManager>.Instance);
    }

    #endregion
}
