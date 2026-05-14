using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ApiGateway.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;

namespace ApiGateway.Controllers;

/// <summary>
/// Development-only endpoint for issuing self-signed JWT tokens.
///
/// In production, tokens should be issued by an external identity provider
/// (configured via Jwt:Authority). This controller enables local testing
/// without an IDP by generating tokens signed with Jwt:SecretKey.
///
/// The controller is only functional when the environment is Development
/// and Jwt:SecretKey is configured.
/// </summary>
[ApiController]
[Route("auth")]
[AllowAnonymous]
public class AuthController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<AuthController> logger)
    {
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>
    /// POST /auth/token
    /// Issues a JWT token for development testing.
    /// Accepts any non-empty username; returns a token valid for 1 hour.
    /// Only available in the Development environment.
    /// </summary>
    [HttpPost("token")]
    public IActionResult GetToken([FromBody] TokenRequest request)
    {
        if (!_environment.IsDevelopment())
            return NotFound();

        var secretKey = _configuration.GetValue<string>("Jwt:SecretKey");
        if (string.IsNullOrWhiteSpace(secretKey))
            return Unauthorized(new { error = "Jwt:SecretKey is not configured" });

        if (string.IsNullOrWhiteSpace(request.Username))
            return BadRequest(new { error = "Username is required" });

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, request.Username),
            new Claim(ClaimTypes.Name, request.Username),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(JwtRegisteredClaimNames.Iat,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64)
        };

        var token = new JwtSecurityToken(
            issuer: "pdf-api-gateway-dev",
            audience: "pdf-api-gateway",
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

        _logger.LogInformation("Issued dev token for user {Username}, expires {Expires}",
            request.Username, token.ValidTo);

        return Ok(new TokenResponse
        {
            Token = tokenString,
            ExpiresAt = token.ValidTo,
            TokenType = "Bearer"
        });
    }
}