using SlothBox.Core;

namespace SlothBox.Tests.Moving;

public sealed class TempFilesTests : IDisposable
{
    private readonly TempFolder _temp = new();

    [Fact]
    public void Each_action_has_its_own_temp_name_beside_the_destination()
    {
        var destination = Path.Combine("D:", "out", "Receipts", "2026-10-20_Jio_599INR.jpg");

        TempFiles.For(destination, 17).ShouldBe(destination + ".17.slothbox-tmp");
        TempFiles.For(destination, 18).ShouldNotBe(TempFiles.For(destination, 17));
    }

    [Fact]
    public void A_free_destination_is_not_taken()
    {
        Directory.CreateDirectory(_temp.PathOf("Receipts"));

        TempFiles.IsTaken(_temp.PathOf("Receipts", "bill.jpg")).ShouldBeFalse();
    }

    [Fact]
    public void A_destination_whose_folder_does_not_exist_yet_is_not_taken() =>
        TempFiles.IsTaken(_temp.PathOf("not-yet", "bill.jpg")).ShouldBeFalse();

    [Fact]
    public void An_existing_file_is_taken()
    {
        var destination = _temp.Write(Path.Combine("Receipts", "bill.jpg"), "a file");

        TempFiles.IsTaken(destination).ShouldBeTrue();
    }

    [Fact]
    public void A_destination_counts_as_taken_while_any_actions_temp_copy_of_it_exists()
    {
        var destination = _temp.PathOf("Receipts", "bill.jpg");
        _temp.Write(Path.Combine("Receipts", "bill.jpg.41.slothbox-tmp"), "another move's copy");

        TempFiles.IsTaken(destination).ShouldBeTrue();
    }

    [Fact]
    public void A_temp_copy_of_a_different_name_does_not_take_the_destination()
    {
        var destination = _temp.PathOf("Receipts", "bill.jpg");
        _temp.Write(Path.Combine("Receipts", "bill-2.jpg.41.slothbox-tmp"), "another name's copy");
        _temp.Write(Path.Combine("Receipts", "bill.jpg.bak"), "a backup");

        TempFiles.IsTaken(destination).ShouldBeFalse();
    }

    public void Dispose() => _temp.Dispose();
}
