namespace MeetingRecorder.Application.Interfaces;

/// <summary>Hashes/verifies passwords. Implemented in Infrastructure (BCrypt).</summary>
public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}
