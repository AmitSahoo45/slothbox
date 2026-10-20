using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pgvector.EntityFrameworkCore;

namespace SlothBox.Data;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SlothBoxDbContext>
{
    public SlothBoxDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SlothBoxDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=5433;Database=slothbox;Username=slothbox",
                options => options.UseVector()).Options);
}
