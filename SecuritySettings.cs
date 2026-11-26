using System.Security.Cryptography;
using System.Text.Json;

namespace NvrVideoLan;

public sealed record SecuritySetupRequest(bool UsePassword, string? Password);
public sealed record SecurityChangeRequest(bool UsePassword, string? Password, string? CurrentPassword);

/// <summary>
/// Persists the user's initial access choice. Never stores a plaintext password.
/// Passwordless mode is meant for loopback access only (enforced in Program.cs).
/// </summary>
public sealed class SecuritySettings
{
    public sealed record StoredSecurity(string Mode, string? Salt, string? Hash, string Revision);
    private readonly object _gate = new();
    private readonly string _file;
    private StoredSecurity? _current;
    private const int Iterations = 210_000;
    private const int HashBytes = 32;

    public SecuritySettings(string dataRoot)
    {
        _file = Path.Combine(dataRoot, "security.json");
        if (!File.Exists(_file)) return;

        var saved = JsonSerializer.Deserialize<StoredSecurity>(File.ReadAllText(_file))
            ?? throw new InvalidDataException("security.json esta vazio ou invalido.");
        if (saved.Mode != "none" && saved.Mode != "password")
            throw new InvalidDataException("Modo de seguranca invalido.");
        if (string.IsNullOrWhiteSpace(saved.Revision))
            throw new InvalidDataException("Revisao de credenciais ausente.");
        if (saved.Mode == "password")
        {
            try
            {
                if (Convert.FromBase64String(saved.Salt ?? "").Length != 16 ||
                    Convert.FromBase64String(saved.Hash ?? "").Length != HashBytes)
                    throw new InvalidDataException("Hash ou salt invalidos em security.json.");
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException("Credenciais de seguranca corrompidas.", ex);
            }
        }
        _current = saved;
    }

    public bool Configured
    {
        get { lock (_gate) return _current is not null; }
    }

    public bool PasswordEnabled
    {
        get { lock (_gate) return _current?.Mode == "password"; }
    }

    // Changing the password invalidates cookies from the previous revision.
    public string Revision
    {
        get { lock (_gate) return _current?.Revision ?? ""; }
    }

    public bool CheckPassword(string? candidate)
    {
        lock (_gate)
        {
            if (_current?.Mode != "password" || candidate is null)
                return false;
            byte[] salt = Convert.FromBase64String(_current.Salt!);
            byte[] expected = Convert.FromBase64String(_current.Hash!);
            byte[] supplied = Rfc2898DeriveBytes.Pbkdf2(candidate, salt,
                Iterations, HashAlgorithmName.SHA256, HashBytes);
            return CryptographicOperations.FixedTimeEquals(supplied, expected);
        }
    }

    public void FirstSetup(SecuritySetupRequest request)
    {
        lock (_gate)
        {
            if (_current is not null)
                throw new InvalidOperationException("A seguranca inicial ja foi configurada.");
            Save(CreateRecord(request.UsePassword, request.Password));
        }
    }

    public void Change(SecurityChangeRequest request)
    {
        lock (_gate)
        {
            if (_current is null)
                throw new InvalidOperationException("Conclua a configuracao inicial primeiro.");
            if (_current.Mode == "password" && !CheckPassword(request.CurrentPassword))
                throw new UnauthorizedAccessException("Senha atual incorreta.");
            Save(CreateRecord(request.UsePassword, request.Password));
        }
    }

    private static StoredSecurity CreateRecord(bool usePassword, string? password)
    {
        if (!usePassword) return new StoredSecurity("none", null, null, Guid.NewGuid().ToString("N"));
        if (string.IsNullOrWhiteSpace(password) || password.Length < 4 || password.Length > 8)
            throw new ArgumentException("Informe uma senha de administrador entre 4 e 8 caracteres.");
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt,
            Iterations, HashAlgorithmName.SHA256, HashBytes);
        return new StoredSecurity("password",
            Convert.ToBase64String(salt), Convert.ToBase64String(hash),
            Guid.NewGuid().ToString("N"));
    }

    private void Save(StoredSecurity next)
    {
        string temp = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(next, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
            File.Move(temp, _file, true);
            _current = next;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
