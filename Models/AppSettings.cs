namespace ExecutePrograms.Models;

public class AppSettings
{
    // xms
    public string XmsMapPath { get; set; } = "";

    // xflow
    public string XflowMapPath { get; set; } = "";

    // PostgreSQL
    public string SqlFilePath { get; set; } = "";
    public string DbHost      { get; set; } = "localhost";
    public string DbPort      { get; set; } = "5432";
    public string DbName      { get; set; } = "xflow";
    public string DbUser      { get; set; } = "postgres";
    public string DbPassword  { get; set; } = "postgres";

    // 프로그램 실행 위치
    public string BatDir { get; set; } = @"C:\Project\Release\xms";
    public string ExeDir { get; set; } = @"C:\Project\xflow\Src\xflowd\bin\Debug\net10.0";
    public string FexDir { get; set; } = @"C:\DAIM\FexHub\Fex\Release\1.14.6.208";

    // FEX 워크스페이스 (비어있으면 Default)
    public string FexWorkspace { get; set; } = "";

    // 체크박스 상태
    public bool BatStart  { get; set; } = true;
    public bool BatStop   { get; set; } = true;
    public bool ExeStart  { get; set; } = true;
    public bool ExeStop   { get; set; } = true;
    public bool FexStart  { get; set; } = true;
    public bool FexStop   { get; set; } = true;
    public bool XmsDbRun  { get; set; } = true;
    public bool XflowDbRun { get; set; } = true;
    public bool SqlRun    { get; set; } = true;

    // 설정 패널 상태
    public bool SettingsExpanded { get; set; } = true;

    // 추가 프로그램 목록
    public List<ExtraProgram> ExtraPrograms { get; set; } = [];
}
