using UpdateHelper.Core.Rules.Subscription;

// 规则包签名工具。所有说明中文。私钥只从命令行指定的仓库外路径读。
if (args.Length == 0) { PrintHelp(); return 0; }

try
{
    switch (args[0])
    {
        case "genkey":
        {
            // genkey <私钥输出.pem> <公钥输出.pem>
            var (priv, pub) = RuleBundleBuilder.GenerateKeyPair();
            File.WriteAllText(args[1], priv);
            File.WriteAllText(args[2], pub);
            Console.WriteLine($"已生成密钥对。私钥：{args[1]}（请妥善保管，切勿提交到仓库）；公钥：{args[2]}");
            return 0;
        }
        case "build":
        {
            // build <规则目录> <版本号> <私钥.pem> <输出目录>
            var rulesDir = args[1];
            var version = int.Parse(args[2]);
            var privPem = File.ReadAllText(args[3]);
            var outDir = args[4];
            Directory.CreateDirectory(outDir);
            var manifest = RuleBundleBuilder.BuildManifest(rulesDir, version);
            var zip = RuleBundleBuilder.BuildZip(rulesDir, manifest);
            var sig = RuleBundleBuilder.Sign(zip, privPem);
            File.WriteAllBytes(Path.Combine(outDir, "rules-bundle.zip"), zip);
            File.WriteAllBytes(Path.Combine(outDir, "rules-bundle.zip.sig"), sig);
            Console.WriteLine($"已打包 v{version}，{manifest.RuleCount} 条规则 → {outDir}\\rules-bundle.zip(.sig)");
            return 0;
        }
        case "builtin-manifest":
        {
            // builtin-manifest <规则目录> <版本号>
            RuleBundleBuilder.WriteBuiltinManifest(args[1], int.Parse(args[2]));
            Console.WriteLine($"已写入内置 manifest.json 到 {args[1]}");
            return 0;
        }
        default:
            PrintHelp();
            return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"出错：{ex.Message}");
    return 1;
}

static void PrintHelp()
{
    Console.WriteLine("""
        规则包签名工具 用法：
          genkey <私钥输出.pem> <公钥输出.pem>              生成 RSA-3072 密钥对
          build  <规则目录> <版本号> <私钥.pem> <输出目录>   打包并签名规则包
          builtin-manifest <规则目录> <版本号>              生成随 app 打包的内置 manifest.json
        """);
}
