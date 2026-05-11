using LinkPocket.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkPocket.Services;

public class VaultService
{
    private readonly LinkPocketDbContext _db;

    public VaultService(LinkPocketDbContext db)
    {
        _db = db;
    }

    public async Task<(List<Password> Passwords, int TotalCount)> GetAllPasswordsAsync(
        string? category = null,
        string? search = null,
        string sortBy = "updated_at",
        string sortOrder = "desc",
        int page = 1,
        int perPage = 20)
    {
        var query = _db.Passwords.AsQueryable();

        if (!string.IsNullOrEmpty(category))
        {
            query = query.Where(p => p.Category == category);
        }

        if (!string.IsNullOrEmpty(search))
        {
            query = query.Where(p => 
                p.Title.Contains(search) ||
                (p.Username != null && p.Username.Contains(search)) ||
                (p.Url != null && p.Url.Contains(search)));
        }

        // 排序
        query = sortBy.ToLower() switch
        {
            "updated_at" => sortOrder == "asc" ? query.OrderBy(p => p.UpdatedAt) : query.OrderByDescending(p => p.UpdatedAt),
            "created_at" => sortOrder == "asc" ? query.OrderBy(p => p.CreatedAt) : query.OrderByDescending(p => p.CreatedAt),
            "title" => sortOrder == "asc" ? query.OrderBy(p => p.Title) : query.OrderByDescending(p => p.Title),
            _ => query.OrderByDescending(p => p.UpdatedAt)
        };

        var totalCount = await query.CountAsync();
        
        var passwords = await query
            .Skip((page - 1) * perPage)
            .Take(perPage)
            .ToListAsync();

        return (passwords, totalCount);
    }

    public async Task<Password> CreatePasswordAsync(string title, string password, 
        string? username = null, string? url = null, string? notes = null, string? category = "general")
    {
        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(password))
            throw new ArgumentException("Title and password are required");

        var entry = new Password
        {
            Title = title,
            Username = username,
            Url = url,
            Notes = notes,
            Category = category,
            StrengthScore = CheckPasswordStrength(password).Score,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        entry.SetPassword(password);

        _db.Passwords.Add(entry);
        await _db.SaveChangesAsync();

        return entry;
    }

    public async Task<Password> UpdatePasswordAsync(int id, string? title = null, 
        string? username = null, string? url = null, string? notes = null, 
        string? category = null, string? password = null)
    {
        var entry = await _db.Passwords.FindAsync(id) 
            ?? throw new Exception("Password not found");

        if (!string.IsNullOrEmpty(title)) entry.Title = title;
        if (username != null) entry.Username = username;
        if (url != null) entry.Url = url;
        if (notes != null) entry.Notes = notes;
        if (category != null) entry.Category = category;

        // 如果更新了密码，重新加密并检测强度
        if (!string.IsNullOrEmpty(password))
        {
            entry.SetPassword(password);
            entry.StrengthScore = CheckPasswordStrength(password).Score;
        }

        entry.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return entry;
    }

    public async Task DeletePasswordAsync(int id)
    {
        var entry = await _db.Passwords.FindAsync(id) 
            ?? throw new Exception("Password not found");

        _db.Passwords.Remove(entry);
        await _db.SaveChangesAsync();
    }

    public async Task<object> GetPasswordDetailAsync(int id)
    {
        var entry = await _db.Passwords.FindAsync(id) 
            ?? throw new Exception("Password not found");

        return new
        {
            id = entry.Id,
            title = entry.Title,
            username = entry.Username,
            password = entry.GetDecryptedPassword(),
            url = entry.Url,
            notes = entry.Notes,
            category = entry.Category,
            strength_score = entry.StrengthScore,
            created_at = entry.CreatedAt,
            updated_at = entry.UpdatedAt
        };
    }

    public PasswordStrengthResult CheckPasswordStrength(string password)
    {
        // 简化的密码强度检测算法（基于常见规则）
        int score = 0;
        List<string> warnings = new();
        List<string> suggestions = new();

        if (password.Length < 8)
        {
            warnings.Add("密码太短");
            suggestions.Add("使用至少8个字符的密码");
        }
        else if (password.Length < 12)
        {
            score += 1;
            suggestions.Add("考虑使用12个或更多字符");
        }
        else
        {
            score += 2;
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(password, @"[a-z]"))
        {
            score += 1;
        }
        else
        {
            warnings.Add("缺少小写字母");
            suggestions.Add("混合使用大小写字母");
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(password, @"[A-Z]"))
        {
            score += 1;
        }
        else
        {
            warnings.Add("缺少大写字母");
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(password, @"\d"))
        {
            score += 1;
        }
        else
        {
            warnings.Add("缺少数字");
            suggestions.Add("添加数字增加安全性");
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(password, @"[!@#$%^&*()_+\-=\[\]{};':""\\|,.<>?]"))
        {
            score += 1;
        }
        else
        {
            warnings.Add("缺少特殊字符");
            suggestions.Add("使用特殊字符如 !@#$%");
        }

        // 常见弱密码检测
        var commonPasswords = new[] { "password", "123456", "qwerty", "abc123", "admin", "letmein" };
        if (commonPasswords.Any(p => password.ToLower().Contains(p)))
        {
            warnings.Add("使用了常见的弱密码模式");
            score = Math.Max(0, score - 2);
        }

        score = Math.Min(4, score);

        return new PasswordStrengthResult
        {
            Score = score,
            Warning = string.Join("; ", warnings),
            Suggestions = suggestions,
            CrackTimeDisplay = score >= 3 ? "数年" : score >= 2 ? "数月" : score >= 1 ? "数天" : "即时"
        };
    }

    public string GenerateStrongPassword(int length = 16, bool includeUppercase = true, 
        bool includeLowercase = true, bool includeNumbers = true, bool includeSymbols = true)
    {
        const string lowercase = "abcdefghijklmnopqrstuvwxyz";
        const string uppercase = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        const string numbers = "0123456789";
        const string symbols = "!@#$%^&*()-_=+[]{}|;:,.<>?";

        var charset = "";
        if (includeLowercase) charset += lowercase;
        if (includeUppercase) charset += uppercase;
        if (includeNumbers) charset += numbers;
        if (includeSymbols) charset += symbols;

        if (string.IsNullOrEmpty(charset))
            charset = lowercase + uppercase + numbers;

        var random = new Random();
        var passwordChars = new char[length];

        for (int i = 0; i < length; i++)
        {
            passwordChars[i] = charset[random.Next(charset.Length)];
        }

        return new string(passwordChars);
    }

    public async Task<List<object>> GetCategoriesAsync()
    {
        return await _db.Passwords
            .GroupBy(p => p.Category ?? "general")
            .Select(g => new { category = g.Key, count = g.Count() })
            .OrderByDescending(g => g.count)
            .Cast<object>()
            .ToListAsync();
    }
}

public class PasswordStrengthResult
{
    public int Score { get; set; }
    public string Warning { get; set; } = "";
    public List<string> Suggestions { get; set; } = new();
    public string CrackTimeDisplay { get; set; } = "未知";
}
