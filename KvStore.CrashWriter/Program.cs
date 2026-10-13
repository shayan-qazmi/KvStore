// "Store" is a short nickname for our KvStore class.
// We need the nickname because in this project the plain word "KvStore"
// means a namespace (a folder-like name), not our class.
using Store = KvStore.Core.KvStore;

// This little program is started (and then killed) by the crash test.
// args[0] = path of the database file
// args[1] = number to start counting from
string path = args[0];
int startIndex = int.Parse(args[1]);

// A big value (20,000 characters). Each record is bigger than the file's
// internal buffer, so it gets written in several steps. That gives the kill
// a real chance to land in the MIDDLE of a record.
// IMPORTANT: the test file has the same line. They must match.
string bigValue = new string('x', 20_000);

// Open the store on the file (this also recovers from earlier crashes).
using var store = new Store(path);

// Loop forever: save key-000000, key-000001, ... one after another.
for (int i = startIndex; ; i++)
{
    store.Put($"key-{i:D6}", $"{i}-{bigValue}");

    // We only print the number AFTER Put has finished.
    // So every number the test reads means "this record was saved".
    Console.WriteLine(i);
}