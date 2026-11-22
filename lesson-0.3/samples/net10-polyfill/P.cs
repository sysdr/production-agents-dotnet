public sealed record A(int X); public sealed record B(string Y);
public union AB(A, B);
public static class P { public static void Main(){ AB v = new A(1); System.Console.WriteLine(v switch { A a => "a", B b => "b" }); } }
