# Inverter

Copy Inverter.cs into your project and start inverting (the control).

```
using Enyim;

var i = new Inverter();
inverter.Add<IService, ServiceImpl>();

var sp = i.Build();

sp.GetRequiredService<IService>().Hello("world");
```

Check `IWiring` for the rest.
