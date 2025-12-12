#include "Utils.h"

char* trim_string(char* buffer, char token)
{
	if (!buffer)
	{
		printf("buffer is NULL!");
		return NULL;
	}
	for (int i = 0; i < strlen(buffer); i++)
	{
		if (buffer[i] == token) {
			buffer[i] = '\0';
			break;
		}
	}
	return buffer;
}


char* get_time_string(char* time_str, int size)
{
	time_t now = time(NULL);
	strftime(time_str, size, "%Y-%m-%d %H:%M:%S", localtime(&now));
	return time_str;
}

int generate_random_number(int max_number)
{
	if (max_number <= 0) return 0;
	int number = 0;
	srand((unsigned int)av_gettime_relative());
	number = rand() % max_number + 1;
	return number;
}

int trim_url_name(char* in, char* out)
{
	if (!in || !out) return -1;
	int i = strlen(in) - 1, j = strlen(in) - 1;

	while (j >= 0 && in[j] != '.')
		j--;

	while (i >= 0 && !(in[i] == '\\' || in[i] == '/'))
		i--;

	int size = j - i - 1; size = size < 0 ? strlen(in + i + 1) : size;

	strncpy(out, in + i + 1, size);
	out[size] = '\0';
	return 0;
}

int write_text_to_file(char* filename, char* text, char* mode)
{
	if (!filename || !text) return -1;
	FILE* fp = fopen(filename, mode);
	if (!fp) return -1;
	fprintf(fp, text);
	fclose(fp);
	return 0;
}


int64_t is_file_changed(char* filename, int64_t last_mtime)//如改变了就返回文件上次修改时间
{
	if (!filename) return -1;
	struct stat buf;
	if (stat(filename, &buf) < 0) return -1;
	if (buf.st_mtime != last_mtime) return buf.st_mtime;
	else return 0;
}


char* join_path(char* env_value, char* filename) {
	if (!env_value) env_value = "";
	if (!filename) filename = "";

	int env_len = strlen(env_value);
	int file_len = strlen(filename);

	// 分配内存
	char* full_path = (char*)av_mallocz(env_len + file_len + 2);
	if (!full_path) return NULL;

	strcpy(full_path, env_value);

	// 标准化路径分隔符
	for (int i = 0; i < env_len; i++) {
		if (full_path[i] == '/' || full_path[i] == '\\') {
			full_path[i] = PATH_SEPARATOR;
		}
	}

	// 添加分隔符（如果需要）
	if (env_len > 0 && full_path[env_len - 1] != PATH_SEPARATOR) {
		strcat(full_path, PATH_SEPARATOR_STR);
	}

	strcat(full_path, filename);
	return full_path;
}




//int 
//// 获取OMNI_VCAM_CONFIG环境变量
//char* config_value = getenv(env_name);
//
//if (config_value == NULL) {
//	fprintf(stderr, "错误: 环境变量 %s 未设置\n", env_name);
//	fprintf(stderr, "请先设置环境变量，例如:\n");
//	fprintf(stderr, "  export OMNI_VCAM_CONFIG='your_config_here'\n");
//	return 1;
//}
//
//printf("成功获取环境变量:\n");
//printf("%s = %s\n", env_name, config_value);
//
//// 检查是否为空字符串
//if (strlen(config_value) == 0) {
//	printf("警告: 环境变量值为空字符串\n");
//}
//
//// 可以根据需要解析配置内容
//// 例如，如果配置是JSON格式，可以使用cJSON库解析
//// 如果是键值对格式，可以使用strtok解析
//
//return 0;