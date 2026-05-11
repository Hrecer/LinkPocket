using LinkPocket.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkPocket.Services;

public class NoteService
{
    private readonly LinkPocketDbContext _db;

    public NoteService(LinkPocketDbContext db)
    {
        _db = db;
    }

    public async Task<List<Note>> GetNotesByLinkIdAsync(int linkId)
    {
        return await _db.Notes
            .Where(n => n.LinkId == linkId)
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync();
    }

    public async Task<Note> CreateNoteAsync(int linkId, string title, string? content = null)
    {
        var link = await _db.Links.FindAsync(linkId) 
            ?? throw new Exception("Link not found");

        var note = new Note
        {
            LinkId = linkId,
            Title = title,
            Content = content,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Notes.Add(note);
        await _db.SaveChangesAsync();

        return note;
    }

    public async Task<Note> UpdateNoteAsync(int noteId, string? title = null, string? content = null)
    {
        var note = await _db.Notes.FindAsync(noteId) 
            ?? throw new Exception("Note not found");

        if (!string.IsNullOrEmpty(title)) note.Title = title;
        if (content != null) note.Content = content;

        note.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return note;
    }

    public async Task DeleteNoteAsync(int noteId)
    {
        var note = await _db.Notes.FindAsync(noteId) 
            ?? throw new Exception("Note not found");

        _db.Notes.Remove(note);
        await _db.SaveChangesAsync();
    }
}
