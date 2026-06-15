import pyodbc

conn_str = "Driver={ODBC Driver 17 for SQL Server};Server=localhost;Database=Eltarshouby-Exam;Trusted_Connection=yes;"
try:
    conn = pyodbc.connect(conn_str)
    cursor = conn.cursor()
    
    print("=== Training Waves ===")
    cursor.execute("SELECT Id, WaveName FROM TrainingWaves")
    for row in cursor.fetchall():
        print(f"Wave ID: {row[0]}, Name: {row[1]}")
        
    print("\n=== Categories ===")
    cursor.execute("SELECT Id, CategoryName FROM Categories")
    for row in cursor.fetchall():
        print(f"Category ID: {row[0]}, Name: {row[1]}")

    print("\n=== Topics ===")
    cursor.execute("SELECT Id, TopicName, CategoryId FROM Topics")
    for row in cursor.fetchall():
        print(f"Topic ID: {row[0]}, Name: {row[1]}, CategoryId: {row[2]}")

    print("\n=== Exams ===")
    cursor.execute("SELECT Id, Title, ExamTypeId, WaveId, IsFinalExam FROM Exams ORDER BY Id DESC")
    for row in cursor.fetchall():
        print(f"Exam ID: {row[0]}, Title: {row[1]}, TypeId: {row[2]}, WaveId: {row[3]}, IsFinal: {row[4]}")
        
except Exception as e:
    print("Error:", e
