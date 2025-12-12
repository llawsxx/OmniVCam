#define _CRT_SECURE_NO_WARNINGS
#pragma once
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <libavutil/mem.h>
#include <libavutil/dict.h>
typedef struct paremeter_table {
	char* type;
	char* name;
	char* content;
}paremeter_table;

typedef struct paremeter_table_context {
	paremeter_table* table;
	int table_size;
}paremeter_table_context;

typedef struct play_list
{
	char* key;//may be NULL
	char* value;//文件名
	AVDictionary* dict;
	struct play_list* previous;
	struct play_list* next;
}play_list;

typedef struct play_list_context
{
	play_list* head;
	play_list* rear;
	struct play_list* current;
	unsigned int total_size;
}play_list_context;

paremeter_table_context* paremeter_table_alloc(int size);
void paremeter_table_free(paremeter_table_context** table_ctx);
int update_config_from_file(char* config_file, paremeter_table_context* table_ctx);
void paremeter_table_print(paremeter_table_context* table_ctx);
char* get_paremeter_table_content(paremeter_table_context* table_ctx, char* type, char* name,int dup_str);
play_list_context* play_list_alloc();
void play_list_free(play_list_context** list);
play_list* play_list_search(play_list_context* list_ctx, char* value); //搜索
int play_list_get_size(play_list_context* list_ctx);
int update_play_list_from_file(play_list_context* list_ctx, char* filename);
play_list* play_list_seek(play_list_context* list_ctx, int seek_index);
int64_t is_file_changed(char* filename, int64_t last_mtime);
char* get_filter_text(char* filename);
//*out_ret : 1:搜索 2：移动位置 3:设置列表循环 4：单曲循环 5:随机播放   1和2时，要看*output内容
int control_input_playing(char* control_file, char* output, int output_size);
char* trim_string(char* buffer, char token);
void trim_space(char* strIn, char* strOut);
char* trim_string(char* buffer, char token);
int parse_params_string_to_dict(const char* params_str, AVDictionary** options_dict, char delimiter);