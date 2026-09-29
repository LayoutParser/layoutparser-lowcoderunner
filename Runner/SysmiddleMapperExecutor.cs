#if SYSMIDDLE
using System;
using System.Collections.Generic;
using SysMiddle.API;                 // APIManager + APIExecutor (SysMiddle.Base.dll)
using SysMiddle.Base.Model.API;      // MapperBasicVO, MapperResultBasicVO

namespace LayoutParserLowCodeRunner
{
    /// <summary>
    /// Bootstrap do SDK Sysmiddle — o SUBSTITUTO do <c>MappersHelper.LoadApiExecutor</c>, sem
    /// <c>appConnector</c>.
    ///
    /// <para><b>O gate de licença tem DUAS partes e as duas são obrigatórias</b> (descoberta por
    /// decompilação de <c>SysMiddle.Base.InstanceFactory</c> + <c>SysMiddle.API.APIManager</c>):</para>
    ///
    /// <list type="number">
    ///   <item><description>O ctor do <c>APIManager</c> pega o <c>ILicenseController</c> de
    ///   <c>InstanceFactory.GetInstance&lt;ILicenseController&gt;()</c>; neste ambiente o
    ///   <c>Initialize()</c> da InstanceFactory NÃO escaneou <c>SysMiddle.ConnectUs.Core.dll</c>,
    ///   então a interface fica sem concreto, <c>GetInstance</c> devolve null e o ctor lança
    ///   "Controle de licença ... não encontrado". Por isso registramos o mapeamento
    ///   interface→concreto EXPLICITAMENTE.</description></item>
    ///   <item><description>Setar <c>APIManager.GlobalConfigurationFileName</c> instancia o
    ///   <c>LicenseController(configLocation)</c> com o <c>global.config</c> (que traz o
    ///   <c>LicenseCode</c>) → licença validada offline, sem VPN e sem SQL.</description></item>
    /// </list>
    ///
    /// <para><b>A ORDEM importa:</b> o ctor do <c>APIManager</c> roda no primeiro acesso a
    /// <c>APIManager.Instance</c> e já exige o license controller pronto e apontado para o
    /// <c>global.config</c>. Registrar/apontar depois é tarde.</para>
    /// </summary>
    internal static class SysmiddleRuntime
    {
        /// <summary>
        /// Prepara o gate de licença e devolve o <c>APIExecutor</c> do package (= identificador do
        /// projeto Sysmiddle, o mesmo <c>&lt;PackageMappers&gt;</c> do <c>config.xml</c> da instância).
        ///
        /// <para>Até 2026-08-10 esse valor vinha de
        /// <c>ConnectorApplicationManager.Instance.GetServerPackage()</c>, e popular esse singleton
        /// era a ÚNICA razão do bootstrap do host (<c>EDocsClientConnectorManager.Start()</c>). Como
        /// o método só devolve uma string de configuração, passar o valor direto elimina o
        /// <c>appConnector</c> inteiro do runner.</para>
        ///
        /// <para>Lança em vez de devolver null: <c>GetApiExecutorByIdentifier</c> engole a falha e
        /// devolve null quando o projeto não casa ou a licença não valida — e o
        /// <c>MappersHelper.LoadApiManager</c> original transformava isso num <b>retry infinito</b>
        /// (<c>while (_apiManager == null)</c>), que é como o runner travava sem dizer o motivo.</para>
        /// </summary>
        public static APIExecutor Create(string globalFolder, string packageGuid)
        {
            if (string.IsNullOrWhiteSpace(packageGuid))
                throw new ArgumentException("packageGuid é obrigatório (ver RunnerArgs.ValidarPackage).", "packageGuid");

            // (1) Mapeamento interface→concreto do controle de licença. Idempotente: CreateType pode
            // ser chamado de novo sem efeito colateral.
            SysMiddle.Base.InstanceFactory.Instance.CreateType(
                typeof(SysMiddle.Base.Interface.ILicenseController),
                typeof(SysMiddle.ConnectUs.Core.Helper.General.LicenseController));

            // (2) global.config → LicenseCode → licença validada offline.
            if (!string.IsNullOrEmpty(globalFolder))
                APIManager.GlobalConfigurationFileName = System.IO.Path.Combine(globalFolder, "global.config");

            var executor = APIManager.Instance.GetApiExecutorByIdentifier(string.Empty, packageGuid);
            if (executor == null)
            {
                throw new SysmiddlePackageNotFoundException(
                    "Package/projeto '" + packageGuid + "' não encontrado nos projetos carregados "
                    + "(global.config: DbProviderType/ConnectionString) ou licença não validada. "
                    + "Confira LowCode:Package contra o <PackageMappers> do config.xml da instância.");
            }

            return executor;
        }
    }

    /// <summary>
    /// Package configurado, porém inexistente/não licenciado — vira
    /// <see cref="RunnerExitCodes.PackageNotFound"/>. Tipo próprio para o Program distinguir isso de
    /// uma falha genérica sem depender de casar texto de mensagem.
    /// </summary>
    /// <summary>Init do SDK falhou (instância/config ausente, licença, DLL) — vira <see cref="RunnerExitCodes.BootstrapFailed"/>.</summary>
    internal sealed class SysmiddleBootstrapException : Exception
    {
        public SysmiddleBootstrapException(Exception inner) : base("Falha no bootstrap do SDK Sysmiddle.", inner) { }
    }

    internal sealed class SysmiddlePackageNotFoundException : Exception
    {
        public SysmiddlePackageNotFoundException(string message) : base(message) { }
    }

    /// <summary>
    /// Executa um mapeador Sysmiddle e devolve o documento transformado.
    ///
    /// <para><b>Replica a <c>MappersHelper.ExecuteMappingDocumentById</c></b>, e não a
    /// <c>ExecuteMappingDocument</c> — a versão anterior deste arquivo tinha portado a segunda, que
    /// é uma função DIFERENTE. As três divergências estão documentadas em
    /// <see cref="SysmiddleDocumentRules"/> e em
    /// <c>docs/architecture/decisao-remover-dependencia-appconnector.md</c> §3. A <c>ById</c> é a
    /// única com equivalência PROVADA contra o gabarito real de <c>.claude/tmp/exemplos/</c>
    /// (4246 chars, byte a byte).</para>
    ///
    /// <para><b>Divergência deliberada, e só no caminho de FALHA:</b> a <c>ById</c> original
    /// envolve tudo num <c>try/catch</c> que loga e devolve <c>string.Empty</c>. Aqui a exceção
    /// SOBE para o <c>Main</c>, que a loga inteira (<c>Fatal</c>) e sai com exit code. Motivo: um
    /// resultado vazio é indistinguível de "mapeador rodou e não casou nada" — foi exatamente esse
    /// silêncio que fez a transformação reportar "completed" tendo transformado zero. O caminho
    /// FELIZ é idêntico byte a byte; só o diagnóstico do erro melhora.</para>
    /// </summary>
    internal sealed class SysmiddleMapperExecutor
    {
        private readonly object _lockObj = new object();
        private readonly APIExecutor _apiExecutor;
        private readonly bool _nfePostProcessing;

        public SysmiddleMapperExecutor(APIExecutor apiExecutor, bool nfePostProcessing = false)
        {
            if (apiExecutor == null) throw new ArgumentNullException("apiExecutor");

            _apiExecutor = apiExecutor;
            _nfePostProcessing = nfePostProcessing;
        }

        /// <summary>Mapeadores do package (modo LIST e fallback de resolução por nome).</summary>
        public Dictionary<string, MapperBasicVO> GetMappers()
        {
            return _apiExecutor.GetMappers();
        }

        /// <summary>
        /// Resolve o mapeador por GUID. Devolve null quando não existe — o
        /// <c>APIExecutor.GetMapperByIdentifier</c> engole qualquer erro e devolve null, então este
        /// null significa "não achei", nunca "explodiu".
        /// </summary>
        public MapperBasicVO GetMapperById(string mapperId)
        {
            return _apiExecutor.GetMapperByIdentifier(mapperId);
        }

        /// <summary>
        /// Resolve o mapeador por nome.
        ///
        /// <para>Tenta primeiro o <c>APIExecutor.GetMapperByName</c> (o caminho do SDK, item 2 da
        /// decisão de arquitetura). Ele compara com <c>StringComparison.Ordinal</c> — case-SENSITIVE.
        /// O runner, porém, sempre resolveu <c>--mapperName</c> de forma case-INsensitive varrendo o
        /// <c>GetMappers()</c>, e a API expõe <c>mapperName</c> no contrato do
        /// <c>TransformationExecutionController</c>. Trocar direto tornaria case-sensitive um
        /// contrato existente, então mantemos o fallback tolerante — logado, para o comportamento
        /// não ficar invisível.</para>
        /// </summary>
        public MapperBasicVO GetMapperByName(string mapperName)
        {
            var exato = _apiExecutor.GetMapperByName(mapperName);
            if (exato != null)
                return exato;

            var mappers = _apiExecutor.GetMappers();
            if (mappers == null)
                return null;

            foreach (var kv in mappers)
            {
                if (kv.Value != null && string.Equals(kv.Value.Name, mapperName, StringComparison.OrdinalIgnoreCase))
                {
                    RunnerLog.Warn("[MAPPER] '{0}' casou apenas ignorando maiusculas/minusculas (nome real: '{1}').",
                        mapperName, kv.Value.Name);
                    return kv.Value;
                }
            }

            return null;
        }

        /// <summary>
        /// Executa o mapeador sobre o documento. Passo a passo idêntico à
        /// <c>ExecuteMappingDocumentById</c> decompilada.
        /// </summary>
        public string ExecuteMappingDocumentById(MapperBasicVO mapper, string document, string fileName)
        {
            if (mapper == null) throw new ArgumentNullException("mapper");

            lock (_lockObj)
            {
                // (1) Declaração XML — COM a exclusão do "<?xml" (divergência §3.1).
                document = SysmiddleDocumentRules.AplicarDeclaracaoXmlSeNecessario(document);

                // (2) ExecuteParser com layout vazio e RESULTADO DESCARTADO (divergência §3.2).
                // Mantido de propósito: pode ter efeito colateral de estado no executor, e remover
                // agora seria risco sem ganho. Não engolimos exceção aqui porque o método do SDK já
                // captura tudo internamente e nunca lança — se um dia lançar, queremos saber.
                _apiExecutor.ExecuteParser(string.Empty, document);

                RunnerLog.Info("Iniciando mapeamento - {0}", mapper.IdentifierGuid);
                var mapperResult = _apiExecutor.ExecuteMapper(mapper.IdentifierGuid, document, true, fileName);
                RunnerLog.Info("Finalizando mapeamento - {0}", mapper.IdentifierGuid);

                if (mapperResult != null && mapperResult.ResultMessage != null)
                {
                    foreach (var message in mapperResult.ResultMessage.GetTransformationResultMessages())
                        RunnerLog.Info("Resultado transformacao Mapeador {0}: {1}", mapper.IdentifierGuid, message.Message);
                }

                var resultado = mapperResult != null ? (mapperResult.TransformedDocument ?? string.Empty) : string.Empty;

                // (3) Pós-processamento NF-e: DESLIGADO por padrão (divergência §3.3).
                return SysmiddleDocumentRules.AplicarPosProcessamentoNFe(resultado, _nfePostProcessing);
            }
        }
    }
}

#endif
