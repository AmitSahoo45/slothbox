using SlothBox.Core;

namespace SlothBox.Data;

internal static class SeedCategories
{
    private static readonly DateTimeOffset SeedDate = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    internal static readonly StoredCategory[] All =
    [
        Row(1, FileKind.Receipt, "Food", "Restaurants, food delivery, groceries"),
        Row(2, FileKind.Receipt, "Shopping", "Online and store purchases"),
        Row(3, FileKind.Receipt, "Bills and recharges", "Electricity, mobile, internet, rent, subscriptions"),
        Row(4, FileKind.Receipt, "Travel", "Tickets, cabs, fuel, hotels"),
        Row(5, FileKind.Receipt, "Other", "Anything that fits no other category", true),
        Row(6, FileKind.Screenshot, "Reddit comments", "Reddit posts and comment threads"),
        Row(7, FileKind.Screenshot, "Instagram comments", "Instagram posts, comments, DMs"),
        Row(8, FileKind.Screenshot, "Chats", "WhatsApp, Telegram, SMS and other chats"),
        Row(9, FileKind.Screenshot, "Code and errors", "Code, terminals, stack traces, error dialogs"),
        Row(10, FileKind.Screenshot, "Other", "Anything that fits no other category", true),
        Row(11, FileKind.Document, "Work", "Work documents and reports"),
        Row(12, FileKind.Document, "Finance", "Statements, tax, insurance, salary"),
        Row(13, FileKind.Document, "Learning", "Articles, notes, courses, documentation"),
        Row(14, FileKind.Document, "Personal", "IDs, letters, personal records"),
        Row(15, FileKind.Document, "Other", "Anything that fits no other category", true),
        Row(16, FileKind.Other, "Installers", "Installers", true),
        Row(17, FileKind.Other, "Archives", "Archives", true),
        Row(18, FileKind.Other, "Media", "Media", true),
        Row(19, FileKind.Other, "Misc", "Other file extensions", true)
    ];

    private static StoredCategory Row(int id, FileKind type, string name, string description, bool system = false) =>
        new() { Id = id, Type = type, Name = name, Description = description, Folder = name,
            IsSystem = system, CreatedAt = SeedDate };
}
