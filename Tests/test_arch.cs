using System;
using Arch.Core;

class Program
{
    static void Main()
    {
        var world = World.Create();
        var components = new object[] { new OpenFPS.Common.Components.Transform(), new OpenFPS.Common.Components.ColliderComponent() };
        var entity = world.Create(components);
        
        int count = 0;
        world.Query(new QueryDescription().WithAll<OpenFPS.Common.Components.Transform>(), (Entity e) => {
            count++;
        });
        Console.WriteLine("Count with Transform: " + count);

        int countObj = 0;
        world.Query(new QueryDescription().WithAll<object[]>(), (Entity e) => {
            countObj++;
        });
        Console.WriteLine("Count with object[]: " + countObj);
    }
}
